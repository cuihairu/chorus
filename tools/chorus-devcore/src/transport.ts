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
  private reconnectTimer: NodeJS.Timeout | null = null;
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
   * when autoReconnect is on). A manual connect during backoff takes over:
   * the scheduled retry is cancelled, so exactly one socket ever exists. */
  connect(): Promise<void> {
    if (this.socket) return Promise.resolve();
    if (this.closing) return Promise.reject(new TransportError("transport is closed"));
    this.closing = false;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.setState("connecting");
    return new Promise<void>((resolve, reject) => {
      this.connectWait = {
        resolve: () => {
          this.connectWait = null;
          resolve();
        },
        reject: (error) => {
          this.connectWait = null;
          reject(error);
        },
      };
      this.openSocket();
    });
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

  private openSocket(): void {
    const socket = net.connect(
      this.endpoint.kind === "uds"
        ? { path: this.endpoint.path }
        : { path: `\\\\.\\pipe\\${this.endpoint.name}` },
    );
    socket.setNoDelay(true);
    this.socket = socket;
    const connectTimer = setTimeout(() => {
      socket.destroy(new TransportError(`connect timed out after ${this.connectTimeoutMs}ms`));
    }, this.connectTimeoutMs);

    socket.once("connect", () => {
      clearTimeout(connectTimer);
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
        continue;
      }
      this.enqueue(envelope);
    }
  }

  private enqueue(envelope: IpcEnvelope): void {
    if (this.queue.length >= this.capacity) {
      this.queue.shift();
      this.droppedFrames += 1;
      if (this.listenerCount("backpressure") > 0) this.emit("backpressure", this.depth);
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
      const envelope = this.queue.shift()!;
      this.emit("envelope", envelope);
      // The typed narrowers throw on a body that misses its shape — a valid
      // envelope with a foreign body is one bad payload, not a drain-crashing
      // error: count it, report it, keep delivering the rest.
      try {
        if (envelope.type === DevMetricsType && this.listenerCount("metrics") > 0) {
          const snapshot = asMetricsSnapshot(envelope);
          if (snapshot) this.emit("metrics", snapshot, envelope);
        } else if (envelope.type === DevLogType && this.listenerCount("log") > 0) {
          const entry = asLogEntry(envelope);
          if (entry) this.emit("log", entry, envelope);
        }
      } catch (error) {
        this.malformedFrames += 1;
        if (this.listenerCount("error") > 0) this.emit("error", error as EnvelopeDecodeError);
      }
      handed += 1;
    }
    if (this.queue.length > 0) this.scheduleDrain();
  }

  private onLinkFailure(error: Error): void {
    if (this.closing) return;
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
    if (this.listenerCount("error") > 0) this.emit("error", error);
    if (!this.autoReconnect) {
      this.setState("closed");
      return;
    }
    const delay = this.backoff.next();
    this.setState("backoff");
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      if (this.closing) return;
      this.setState("connecting");
      this.openSocket();
    }, delay);
  }

  private teardownSocket(): void {
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
