// The devtools attach transport (IDE-EXT): one socket to the engine's
// optional devtools channel — a unix domain socket on posix
// ({dir}/{prefix}_devtools.sock) or a named pipe on Windows
// (\\.\pipe\{prefix}.devtools) — the same endpoints DevToolsService binds
// (src/Chorus.DevTools/DevToolsService.cs, driven by the host's --devtools
// flag). The plane is push-only: no handshake, no requests; dev.metrics and
// dev.log frames arrive at the engine's cadence and every attached IDE sees
// the same bytes (one serialization broadcast to all).
//
// Backpressure: decoded frames cross to the consumer through a bounded
// drop-oldest queue drained one batch per event-loop turn. The bound mirrors
// the engine's own log-queue discipline (DropOldest in DevToolsService —
// logging never blocks there), and deliberately avoids socket.pause(): a
// stalled reader is dropped by the engine after its 2 s write timeout
// (DevToolsService.WriteTimeout), so holding the kernel buffer hostage risks
// losing the whole attach; dropping the oldest local frames keeps the link
// flowing and the newest telemetry arriving.

import { EventEmitter } from "node:events";
import * as net from "node:net";
import * as path from "node:path";
import { FrameAccumulator, OversizeFrameError } from "./frame.js";
import {
  asLogEntry,
  asMetricsSnapshot,
  decodeEnvelope,
  DevLogType,
  DevMetricsType,
  EnvelopeDecodeError,
  IpcEnvelope,
} from "./envelope.js";
import { BackoffOptions, ReconnectBackoff } from "./backoff.js";

export type LinkState = "idle" | "connecting" | "ready" | "backoff" | "closed";

/** The engine's devtools endpoint: the UDS file on posix, the pipe name on Windows. */
export type AttachEndpoint =
  | { kind: "uds"; path: string }
  | { kind: "pipe"; name: string };

/** Derives the endpoint the engine binds, mirroring DevToolsService's own
 * defaults (empty dir falls to /tmp/chorus, empty prefix to "chorus"). */
export function devtoolsEndpoint(
  platform: NodeJS.Platform,
  unixSocketDir = "/tmp/chorus",
  pipePrefix = "chorus",
): AttachEndpoint {
  const dir = unixSocketDir.trim().length === 0 ? "/tmp/chorus" : unixSocketDir;
  const prefix = pipePrefix.trim().length === 0 ? "chorus" : pipePrefix;
  return platform === "win32"
    ? { kind: "pipe", name: `${prefix}.devtools` }
    : { kind: "uds", path: path.join(dir, `${prefix}_devtools.sock`) };
}

export interface TransportOptions {
  endpoint: AttachEndpoint;
  /** Reconnect with backoff after an unexpected close. Default true. */
  autoReconnect?: boolean;
  backoff?: BackoffOptions;
  connectTimeoutMs?: number;
  /** The drop-oldest bound on undelivered decoded frames. Default 4096. */
  queueCapacity?: number;
  /** Frames handed to the consumer per event-loop turn. Default 64. */
  drainBatchSize?: number;
}

export interface QueueDepth {
  size: number;
  capacity: number;
  dropped: number;
}

export interface TransportStats extends QueueDepth {
  skippedEmptyFrames: number;
  /** Frame-level decode misses plus typed bodies that missed their shape —
   * everything the link carried that no consumer could read. */
  malformedFrames: number;
}

export class TransportError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "TransportError";
  }
}

export type DevToolsEvents = {
  /** Every decoded envelope, in delivery order (drop-oldest aside). */
  envelope: (envelope: IpcEnvelope) => void;
  metrics: (snapshot: NonNullable<ReturnType<typeof asMetricsSnapshot>>, envelope: IpcEnvelope) => void;
  log: (entry: NonNullable<ReturnType<typeof asLogEntry>>, envelope: IpcEnvelope) => void;
  state: (state: LinkState) => void;
  /** The queue overflowed; the payload carries the depth and drop count. */
  backpressure: (depth: QueueDepth) => void;
  /** Link failures; the transport reconnects unless autoReconnect is off. */
  error: (error: Error) => void;
};

export class DevToolsTransport extends EventEmitter {
  private readonly endpoint: AttachEndpoint;
  private readonly autoReconnect: boolean;
  private readonly connectTimeoutMs: number;
  private readonly capacity: number;
  private readonly batchSize: number;
  private readonly backoff: ReconnectBackoff;
  /** Frame bytes of the CURRENT stream. Rebuilt at every connect: a partial
   * frame from a dead link must never meet the next link's bytes — the two
   * streams share nothing. */
  private frames = new FrameAccumulator();

  private socket: net.Socket | null = null;
  private stateValue: LinkState = "idle";
  private connectWait: { resolve: () => void; reject: (error: Error) => void } | null = null;
  private pendingConnect: Promise<void> | null = null;
  /** A dial registration in progress: reentrant connects inside it —
   * including a hook firing inside the attempt's own Promise construction,
   * before pendingConnect exists — join the claim and settle with the
   * attempt instead of dialing beside it. */
  private connectClaim: { joins: { resolve: (value: void) => void; reject: (error: Error) => void }[] } | null = null;
  private reconnectTimer: NodeJS.Timeout | null = null;
  /** The in-flight dial's deadline (FIX-IDE-8): a dial that fails or is
   * cancelled must not leave it running — a referenced 5s timer pins the
   * socket and the whole transport past shutdown in standalone consumers. */
  private connectTimer: NodeJS.Timeout | null = null;
  private drainScheduled = false;
  private closing = false;

  private readonly queue: IpcEnvelope[] = [];
  private droppedFrames = 0;
  private skippedEmptyFrames = 0;
  private malformedFrames = 0;

  constructor(options: TransportOptions) {
    super();
    this.endpoint = options.endpoint;
    this.autoReconnect = options.autoReconnect !== false;
    this.connectTimeoutMs = options.connectTimeoutMs ?? 5000;
    this.capacity = Math.max(1, options.queueCapacity ?? 4096);
    this.batchSize = Math.max(1, options.drainBatchSize ?? 64);
    this.backoff = new ReconnectBackoff(options.backoff);
  }

  get state(): LinkState {
    return this.stateValue;
  }

  get stats(): TransportStats {
    return {
      size: this.queue.length,
      capacity: this.capacity,
      dropped: this.droppedFrames,
      skippedEmptyFrames: this.skippedEmptyFrames,
      malformedFrames: this.malformedFrames,
    };
  }

  /** Opens the socket. Resolves when the engine's endpoint answers; rejects
   * if the first attempt fails (reconnection continues in the background
   * when autoReconnect is on). A manual connect during backoff joins the
   * retry's in-flight attempt rather than dialing beside it. Concurrent
   * callers share the one attempt — nobody is told the link is up while
   * the dial is still pending, and exactly one socket ever exists. */
  connect(): Promise<void> {
    if (this.socket && this.stateValue === "ready") return Promise.resolve();
    if (this.closing) return Promise.reject(new TransportError("transport is closed"));
    if (this.connectClaim) return this.joinClaim();
    if (this.pendingConnect) return this.pendingConnect;
    this.closing = false;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    return this.beginConnect();
  }

  /** Settles with the attempt the claim's owner builds, however it ends —
   * the joiner never dials and never settles before the attempt does. */
  private joinClaim(): Promise<void> {
    const claim = this.connectClaim!;
    return new Promise<void>((resolve, reject) => {
      claim.joins.push({ resolve, reject });
    });
  }

  /** The one dial-at-a-time mechanism: state to connecting, one waiter, one
   * socket. Manual connects and automatic retries both go through here, so
   * a shell that attaches mid-retry rides the same promise the loop made.
   * The single-attempt invariant lives here, not only in connect()'s guards:
   * a retry timer that fires beside a live dial (or any caller reaching past
   * connect()) rides the in-flight attempt or an established link — it never
   * opens a socket beside one. */
  private beginConnect(): Promise<void> {
    if (this.pendingConnect) return this.pendingConnect;
    if (this.socket && this.stateValue === "ready") return Promise.resolve();
    // The attempt registers in full — waiter and in-flight promise — before
    // the dial starts, because the dial's synchronous segment can reenter:
    // Node publishes the net.client.socket diagnostics channel before
    // net.connect/socket.connect return, so a subscriber there observes the
    // transport mid-registration. A connect() in that window must find a
    // joinable attempt (one socket), and a close() there must land on a
    // fully built one (no socket left alive behind a closed transport).
    // The claim lands first of all: even the attempt's own Promise
    // construction publishes an async_hooks.init before the promise exists,
    // and a connect() in that window has no promise to join yet.
    const claim: { joins: { resolve: (value: void) => void; reject: (error: Error) => void }[] } = {
      joins: [],
    };
    this.connectClaim = claim;
    let settleResolve!: () => void;
    let rejectAttempt!: (error: Error) => void;
    const attempt = new Promise<void>((resolve, reject) => {
      settleResolve = resolve;
      rejectAttempt = reject;
    });
    this.connectWait = {
      resolve: () => {
        this.connectWait = null;
        this.pendingConnect = null;
        settleResolve();
      },
      reject: (error) => {
        this.connectWait = null;
        this.pendingConnect = null;
        rejectAttempt(error);
      },
    };
    this.pendingConnect = attempt;
    // Register the waiter and the promise BEFORE releasing the claim: the
    // joiner bindings below create promises of their own, and a connect()
    // in one of those windows must still find something to join. The
    // claim-to-registered transition is what every window above relied on
    // being continuous — keep it that way.
    this.connectClaim = null;
    // Joiners created during the construction window settle with the
    // attempt: one dial, one settlement, nobody left with an orphaned
    // promise.
    for (const join of claim.joins) attempt.then(join.resolve, join.reject);
    // close() is what marks a cancel, and connect() cleared the flag before
    // calling here: any synchronous close anywhere in this registration —
    // promise construction, the deadline's timer creation, the dial's
    // diagnostics publish — is visible on the flag whether or not it found
    // fields already written.
    if (this.closing) {
      // A close inside the promise's own async_hooks.init found no waiter
      // to reject, and the registration it skipped must not keep a dead
      // attempt standing in for later connects.
      if (this.connectWait) this.connectWait.reject(new TransportError("transport closed"));
      else rejectAttempt(new TransportError("transport closed"));
      return attempt;
    }
    const socket = this.openSocket();
    if (this.closing) {
      // A dial-time close tears the dial down and rejects the waiter, but
      // Node swallows a destroy issued inside the dial's synchronous
      // segment and a deadline timer made across the close lands in the
      // field afterwards. Finish the teardown here — after the window — so
      // no socket outlives the closed state and no deadline is pinned
      // behind it. Announcing "connecting" over "closed" would resurrect a
      // dead attempt; the caller already got the rejection.
      this.teardownSocket();
      if (socket && this.socket !== socket) {
        socket.removeAllListeners();
        socket.destroy();
      }
      return attempt;
    }
    this.setState("connecting");
    return attempt;
  }

  /** Closes the transport and cancels any scheduled reconnect. */
  close(): void {
    this.closing = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.teardownSocket();
    if (this.connectWait) {
      const waiter = this.connectWait;
      this.connectWait = null;
      waiter.reject(new TransportError("transport closed"));
    }
    this.queue.length = 0;
    this.setState("closed");
  }

  private setState(state: LinkState): void {
    if (this.stateValue === state) return;
    this.stateValue = state;
    this.emit("state", state);
  }

  private openSocket(): net.Socket | null {
    // The socket is built and registered before it dials: socket.connect()
    // is where synchronous reentrancy lives (the net diagnostics channel
    // publishes before it returns), so ownership, deadline and listeners are
    // all in place by then — a subscriber that closes during the call finds
    // a teardown-able socket, and one that reconnects joins the registered
    // attempt instead of dialing beside it. The socket is returned so the
    // caller can finish a teardown that a mid-dial close started: Node
    // swallows a destroy issued inside the dial's synchronous segment.
    const socket = new net.Socket();
    socket.setNoDelay(true);
    this.socket = socket;
    this.connectTimer = setTimeout(() => {
      socket.destroy(new TransportError(`connect timed out after ${this.connectTimeoutMs}ms`));
    }, this.connectTimeoutMs);
    // A close landing inside the timer's own creation saw a null field and
    // cleared nothing, then this assignment landed behind it: a deadline
    // pinning the transport past shutdown. Created across a close, it never
    // was needed — drop it instead of keeping it.
    if (this.closing) this.clearConnectTimer();

    socket.once("connect", () => {
      this.clearConnectTimer();
      // A fresh stream carries fresh bytes: drop any partial frame the
      // previous link left in the accumulator.
      this.frames = new FrameAccumulator();
      this.backoff.reset();
      this.setState("ready");
      if (this.connectWait) {
        const waiter = this.connectWait;
        this.connectWait = null;
        waiter.resolve();
      }
    });
    socket.on("data", (chunk: Buffer) => this.onData(chunk));
    socket.once("error", (error: Error) => this.onLinkFailure(error));
    socket.once("close", () => this.onLinkFailure(new TransportError("connection closed")));
    // A dial-time close() already nulls the field and tears the socket
    // down; skipping the dial on a superseded object avoids dialing a
    // destroyed socket outright.
    if (this.socket !== socket) return socket;
    socket.connect(
      this.endpoint.kind === "uds"
        ? { path: this.endpoint.path }
        : { path: `\\\\.\\pipe\\${this.endpoint.name}` },
    );
    return socket;
  }

  private onData(chunk: Buffer): void {
    let decoded: ReturnType<FrameAccumulator["push"]>;
    try {
      decoded = this.frames.push(chunk);
    } catch (error) {
      // An oversize length means the stream lost frame alignment — the
      // engine tears the connection down the same way; a fresh socket is
      // the only recovery.
      this.onLinkFailure(error as OversizeFrameError);
      return;
    }
    for (const frame of decoded) {
      if (frame.payload.length === 0) {
        // Legal on the wire (FrameReader), carries nothing for this plane.
        this.skippedEmptyFrames += 1;
        continue;
      }
      let envelope: IpcEnvelope;
      try {
        envelope = decodeEnvelope(frame.payload);
      } catch (error) {
        // A malformed payload is one bad frame, not a broken stream: the
        // length prefix keeps the accumulator aligned, so skip it and keep
        // reading.
        this.malformedFrames += 1;
        if (this.listenerCount("error") > 0) this.emit("error", error as EnvelopeDecodeError);
        // A handler that closed the transport during the report ends this
        // chunk: close() emptied the queue, and the frames behind it in this
        // call stack would re-seed a closed transport's queue.
        if (this.closing) return;
        continue;
      }
      this.enqueue(envelope);
      // The enqueue itself can close (a backpressure handler): the rest of
      // this chunk belongs to the dead stack.
      if (this.closing) return;
    }
  }

  private enqueue(envelope: IpcEnvelope): void {
    if (this.queue.length >= this.capacity) {
      this.queue.shift();
      this.droppedFrames += 1;
      if (this.listenerCount("backpressure") > 0) this.emit("backpressure", this.depth);
      // A backpressure handler that closed the transport emptied the queue;
      // this frame dies with the old stack instead of re-seeding it.
      if (this.closing) return;
    }
    this.queue.push(envelope);
    this.scheduleDrain();
  }

  private get depth(): QueueDepth {
    return { size: this.queue.length, capacity: this.capacity, dropped: this.droppedFrames };
  }

  private scheduleDrain(): void {
    if (this.drainScheduled) return;
    this.drainScheduled = true;
    setImmediate(() => {
      this.drainScheduled = false;
      this.drain();
    });
  }

  private drain(): void {
    let handed = 0;
    while (this.queue.length > 0 && handed < this.batchSize) {
      // A handler that closed the transport mid-batch ends the old stack:
      // close() emptied the queue and nothing this call still holds may be
      // delivered behind the closed state.
      if (this.closing) return;
      const envelope = this.queue.shift()!;
      this.emit("envelope", envelope);
      // A close during the envelope delivery still owns the typed emits for
      // this frame — a closed transport delivers nothing further.
      if (this.closing) return;
      // The typed narrowers throw on a body that misses its shape — a valid
      // envelope with a foreign body is one bad payload, not a drain-crashing
      // error: count it, report it, keep delivering the rest. The emits sit
      // outside this handler on purpose: a consumer that throws is a consumer
      // problem, never a malformed frame, and a throw there must not reach
      // this catch — that would count a good frame bad and re-publish an
      // error behind a closed transport.
      let event: "metrics" | "log" | null = null;
      let typed: unknown;
      try {
        if (envelope.type === DevMetricsType && this.listenerCount("metrics") > 0) {
          const snapshot = asMetricsSnapshot(envelope);
          if (snapshot) {
            event = "metrics";
            typed = snapshot;
          }
        } else if (envelope.type === DevLogType && this.listenerCount("log") > 0) {
          const entry = asLogEntry(envelope);
          if (entry) {
            event = "log";
            typed = entry;
          }
        }
      } catch (error) {
        this.malformedFrames += 1;
        if (this.listenerCount("error") > 0) this.emit("error", error as EnvelopeDecodeError);
        // An error listener that closed ends this batch here.
        if (this.closing) return;
        continue;
      }
      try {
        if (event === "metrics") this.emit("metrics", typed as NonNullable<ReturnType<typeof asMetricsSnapshot>>, envelope);
        else if (event === "log") this.emit("log", typed as NonNullable<ReturnType<typeof asLogEntry>>, envelope);
      } catch (error) {
        // A consumer throwing over a well-formed frame is a consumer
        // problem, never a malformed frame: report it, keep the batch
        // moving — and never re-publish after an explicit close.
        if (!this.closing && this.listenerCount("error") > 0) this.emit("error", error as Error);
      }
      // The same for a close during the typed emit or its error report.
      if (this.closing) return;
      handed += 1;
    }
    if (this.queue.length > 0) this.scheduleDrain();
  }

  private onLinkFailure(error: Error): void {
    if (this.closing) return;
    // 'error' and 'close' both fire for one dead socket: one failure gets
    // one retry. The second callback finds the socket already torn down —
    // scheduling past it would leak a parallel retry chain that dials
    // beside the loop's own timer.
    if (this.socket === null) return;
    this.teardownSocket();
    // The dead link's undelivered frames die with it: a queued frame that
    // lands after the state transition would have handlers mark sources
    // "up" over a "closed"/"waiting" status — telemetry resurrecting a
    // source that has nothing behind it.
    this.queue.length = 0;
    if (this.connectWait) {
      const waiter = this.connectWait;
      this.connectWait = null;
      waiter.reject(error);
    }
    if (!this.autoReconnect) {
      this.setState("closed");
      if (this.listenerCount("error") > 0) this.emit("error", error);
      return;
    }
    // The failure's bookkeeping lands before any callback runs — the retry
    // schedule is stored, then the backoff transition, then the error: a
    // state listener that synchronously close()s during "backoff" finds and
    // clears the freshly scheduled retry (no timer survives the close), an
    // error listener that connect()s cancels it and dials exactly once, and
    // neither meets a half-finished failure path that would overwrite its
    // decision afterwards.
    const delay = this.backoff.next();
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      if (this.closing) return;
      // The retry dials through the shared-attempt mechanism too: a manual
      // connect arriving mid-dial joins this attempt instead of opening a
      // second socket that close() would never hang up, and the mechanism's
      // own invariant refuses a second dial beside a live attempt (a timer
      // that fires after a listener's takeover adds nothing). The background
      // retry has no awaiter of its own — the failure is already reported
      // through the error event and the state machine — so the rejection
      // is absorbed here.
      this.beginConnect().catch(() => {});
    }, delay);
    // A close inside the timer's own async_hooks.init cleared nothing: the
    // assignment landed behind it, and a referenced retry timer pins the
    // transport past shutdown — the backoff state over the closed one
    // would resurrect a dead failure. Drop the timer and hold the closed
    // state; the failure was already reported when it happened.
    if (this.closing) {
      this.clearReconnectTimer();
      return;
    }
    this.setState("backoff");
    if (this.listenerCount("error") > 0) this.emit("error", error);
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private clearConnectTimer(): void {
    if (this.connectTimer) {
      clearTimeout(this.connectTimer);
      this.connectTimer = null;
    }
  }

  private teardownSocket(): void {
    // The dial's deadline dies with the dial: teardown runs on failure and
    // on close, so a failed or cancelled attempt never keeps the timer —
    // and the socket it retains — alive past the transport's shutdown.
    this.clearConnectTimer();
    if (this.socket) {
      const socket = this.socket;
      this.socket = null;
      socket.removeAllListeners();
      socket.destroy();
    }
  }
}

// Typed emit/on helpers: EventEmitter's on() is stringly typed; narrow it so
// IDE hosts get completion without a typed-emitter dependency.
export interface DevToolsTransport {
  on<Event extends keyof DevToolsEvents>(event: Event, listener: DevToolsEvents[Event]): this;
  emit<Event extends keyof DevToolsEvents>(event: Event, ...args: Parameters<DevToolsEvents[Event]>): boolean;
}
