// The attach session (IDE-EXT): one facade over one DevToolsTransport for IDE
// shells — attach, subscribe, detach. Nothing here mutates the engine; every
// surface is read-only (dev.metrics snapshots, dev.log entries, link state).
// The VS Code extension (and any future VS host) stays thin: build options,
// attach, render events.

import { EventEmitter } from "node:events";
import { DevLogEntry, DevMetricsSnapshot, IpcEnvelope } from "./envelope.js";
import {
  AttachEndpoint,
  DevToolsTransport,
  devtoolsEndpoint,
  LinkState,
  QueueDepth,
  TransportOptions,
  TransportStats,
} from "./transport.js";

export type SourceState = "up" | "waiting" | "off";

export interface AttachStatus {
  link: LinkState;
  /** "waiting" until the first dev.metrics frame arrives, "up" after. */
  metrics: SourceState;
  /** "waiting" until the first dev.log frame arrives, "up" after. */
  log: SourceState;
}

export interface AttachOptions {
  transport: TransportOptions | DevToolsTransport;
}

export type AttachEvents = {
  metrics: (snapshot: DevMetricsSnapshot, envelope: IpcEnvelope) => void;
  log: (entry: DevLogEntry, envelope: IpcEnvelope) => void;
  "link-state": (state: LinkState) => void;
  "source-state": (status: AttachStatus) => void;
  backpressure: (depth: QueueDepth) => void;
  error: (error: Error) => void;
};

export class AttachSession extends EventEmitter {
  private readonly transport: DevToolsTransport;
  private statusValue: AttachStatus = { link: "idle", metrics: "off", log: "off" };
  private lastMetricsValue: DevMetricsSnapshot | null = null;
  private lastLogValue: DevLogEntry | null = null;

  constructor(options: AttachOptions) {
    super();
    this.transport =
      options.transport instanceof DevToolsTransport
        ? options.transport
        : new DevToolsTransport(options.transport);

    this.transport.on("state", (state) => {
      this.statusValue.link = state;
      if (state === "closed") {
        // A closed session is done: the shells read the sources as off.
        this.setSource("metrics", "off");
        this.setSource("log", "off");
      } else if (state !== "ready") {
        // Between links nothing arrives: attached sources fall back to
        // waiting, not off — they recover by themselves on the next link.
        this.setSource("metrics", this.statusValue.metrics === "off" ? "off" : "waiting");
        this.setSource("log", this.statusValue.log === "off" ? "off" : "waiting");
      }
      this.emit("link-state", state);
      this.emit("source-state", this.status);
    });
    // Typed events, not raw envelopes: the transport's drain is where a body
    // that misses its shape gets narrowed exactly once — counted in
    // malformedFrames, reported through a listener-guarded error, session
    // alive. Narrowing here too would double-count and re-break the crash
    // the transport already guards against.
    this.transport.on("metrics", (snapshot, envelope) => {
      this.lastMetricsValue = snapshot;
      this.setSource("metrics", "up");
      this.emit("metrics", snapshot, envelope);
    });
    this.transport.on("log", (entry, envelope) => {
      this.lastLogValue = entry;
      this.setSource("log", "up");
      this.emit("log", entry, envelope);
    });
    this.transport.on("backpressure", (depth) => this.emit("backpressure", depth));
    this.transport.on("error", (error) => {
      // The transport guards this emission the same way; Node throws on an
      // 'error' event without listeners, so a shell that does not care never
      // turns a link failure into a process crash.
      if (this.listenerCount("error") > 0) this.emit("error", error);
    });
  }

  /** Derives the endpoint the engine binds for the given platform. */
  static endpoint(platform: NodeJS.Platform, unixSocketDir?: string, pipePrefix?: string): AttachEndpoint {
    return devtoolsEndpoint(platform, unixSocketDir, pipePrefix);
  }

  get status(): AttachStatus {
    return { ...this.statusValue };
  }

  /** The freshest dev.metrics snapshot, or null before the first one. */
  get lastMetrics(): DevMetricsSnapshot | null {
    return this.lastMetricsValue;
  }

  /** The freshest dev.log entry, or null before the first one. */
  get lastLog(): DevLogEntry | null {
    return this.lastLogValue;
  }

  /** The transport's honest counters (drops, malformed, skipped-empty) —
   * shells surface them so a lossy stretch never looks clean. */
  get stats(): TransportStats {
    return this.transport.stats;
  }

  /** Connects (and keeps reconnecting) until detach. The sources read
   * "waiting" from here until their first frames arrive. */
  attach(): Promise<void> {
    this.setSource("metrics", "waiting");
    this.setSource("log", "waiting");
    this.emit("source-state", this.status);
    return this.transport.connect();
  }

  /** Closes the link and stops reconnection. The session is done; a fresh
   * AttachSession carries a fresh attach. */
  detach(): void {
    this.transport.close();
  }

  private setSource(source: "metrics" | "log", state: SourceState): void {
    if (this.statusValue[source] === state) return;
    this.statusValue[source] = state;
    this.emit("source-state", this.status);
  }
}

export interface AttachSession {
  on<Event extends keyof AttachEvents>(event: Event, listener: AttachEvents[Event]): this;
  emit<Event extends keyof AttachEvents>(event: Event, ...args: Parameters<AttachEvents[Event]>): boolean;
}
