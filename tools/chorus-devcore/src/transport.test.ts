import * as fs from "node:fs/promises";
import * as net from "node:net";
import * as os from "node:os";
import * as path from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { encodeFrame, MAX_FRAME_BYTES, OversizeFrameError } from "./frame.js";
import { DevLogEntry, DevMetricsSnapshot, DevToolsSession, EnvelopeDecodeError, IpcEnvelope } from "./envelope.js";
import { devtoolsEndpoint, DevToolsTransport, LinkState } from "./transport.js";

/** A stand-in engine devtools channel: the same accept-and-push shape
 * DevToolsService runs, so the transport tests ride real sockets. */
class FakeEngine {
  private server = net.createServer((client) => {
    this.clients.push(client);
    this.connections += 1;
    client.on("close", () => {
      this.closedClients += 1;
    });
    client.on("error", () => {});
  });
  private clients: net.Socket[] = [];
  /** Every client the endpoint ever accepted, including closed ones. */
  connections = 0;
  /** Accepted clients whose socket has fully closed — the hang-up ledger. */
  closedClients = 0;
  readonly socketPath: string;

  constructor(readonly dir: string) {
    this.socketPath = path.join(dir, "chorus_devtools.sock");
  }

  listen(): Promise<void> {
    return new Promise((resolve, reject) => {
      this.server.once("error", reject);
      this.server.listen(this.socketPath, () => resolve());
    });
  }

  send(envelope: Record<string, unknown>): void {
    const wire = encodeFrame(JSON.stringify(envelope));
    for (const client of this.clients) client.write(wire);
  }

  sendRaw(wire: Buffer): void {
    for (const client of this.clients) client.write(wire);
  }

  dropClients(): void {
    for (const client of this.clients) client.destroy();
    this.clients = [];
  }

  close(): Promise<void> {
    this.dropClients();
    return new Promise<void>((resolve) => this.server.close(() => resolve())).then(async () => {
      try {
        await fs.unlink(this.socketPath);
      } catch {
        // already gone
      }
    });
  }
}

const dirs: string[] = [];

afterEach(async () => {
  while (dirs.length > 0) {
    await fs.rm(dirs.pop()!, { recursive: true, force: true });
  }
});

async function newDir(): Promise<string> {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), "devcore-"));
  dirs.push(dir);
  return dir;
}

function envelopeOf(type: string, body: Record<string, unknown>): Record<string, unknown> {
  return {
    type,
    messageId: `m_${Math.random().toString(16).slice(2).padStart(12, "0")}`,
    sessionId: DevToolsSession,
    createdTimeMs: Date.now(),
    body,
  };
}

const metricsBody = () => ({
  cpuPercent: 12.5,
  managedMemoryBytes: 3051736,
  workingSetBytes: 88104960,
  threadCount: 34,
  processId: process.pid,
  timestampMs: Date.now(),
});

const logBody = () => ({
  level: "Information",
  category: "Test.Engine",
  timestampMs: Date.now(),
  body: "Now listening on: http://127.0.0.1:1",
});

async function waitForState(transport: DevToolsTransport, wanted: LinkState, timeoutMs = 10000): Promise<void> {
  if (transport.state === wanted) return;
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => {
      transport.off("state", onState);
      reject(new Error(`state never reached ${wanted} (at ${transport.state})`));
    }, timeoutMs);
    const onState = (state: LinkState) => {
      if (state !== wanted) return;
      clearTimeout(timer);
      transport.off("state", onState);
      resolve();
    };
    transport.on("state", onState);
  });
}

describe("transport", () => {
  it("delivers typed metrics and log events off a real socket", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
    });

    const metrics: DevMetricsSnapshot[] = [];
    const logs: DevLogEntry[] = [];
    transport.on("metrics", (snapshot) => metrics.push(snapshot));
    transport.on("log", (entry) => logs.push(entry));
    await transport.connect();
    expect(transport.state).toBe("ready");

    engine.send(envelopeOf("dev.metrics", metricsBody()));
    engine.send(envelopeOf("dev.log", logBody()));
    await vi.waitFor(() => expect(metrics.length).toBe(1), { timeout: 10000, interval: 5 });
    expect(logs).toHaveLength(1);
    expect(metrics[0]!.cpuPercent).toBeCloseTo(12.5);
    expect(metrics[0]!.processId).toBe(process.pid);
    expect(logs[0]!.level).toBe("Information");

    transport.close();
    await engine.close();
  });

  it("reconnects with backoff until the engine endpoint returns", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      backoff: { initialMs: 30, maxMs: 200, multiplier: 2 },
    });
    await transport.connect();

    // The engine goes away (rolling restart): the link falls to backoff and
    // retries on its own.
    await engine.close();
    await waitForState(transport, "backoff");

    // The engine returns before the next attempt lands.
    const returned = new FakeEngine(dir);
    await returned.listen();
    await waitForState(transport, "ready");
    expect(transport.state).toBe("ready");

    const metrics: DevMetricsSnapshot[] = [];
    transport.on("metrics", (snapshot) => metrics.push(snapshot));
    returned.send(envelopeOf("dev.metrics", metricsBody()));
    await vi.waitFor(() => expect(metrics.length).toBe(1), { timeout: 10000, interval: 5 });

    transport.close();
    await returned.close();
  });

  it("drops the oldest frames under backpressure and keeps the link alive", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const capacity = 8;
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
      queueCapacity: capacity,
    });

    const depthReports: number[] = [];
    transport.on("backpressure", (depth) => depthReports.push(depth.dropped));
    const delivered: IpcEnvelope[] = [];
    transport.on("envelope", (envelope) => delivered.push(envelope));
    await transport.connect();

    // One synchronous burst: 50 frames against a capacity of 8, all parsed
    // inside onData before any drain turn.
    const sent = Array.from({ length: 50 }, (_, i) => envelopeOf("dev.metrics", { ...metricsBody(), cpuPercent: i }));
    engine.sendRaw(Buffer.concat(sent.map((e) => encodeFrame(JSON.stringify(e)))));
    // Every frame lands on one side of the ledger before judging.
    await vi.waitFor(() => expect(delivered.length + transport.stats.dropped).toBe(50), {
      timeout: 10000,
      interval: 5,
    });
    await new Promise((resolve) => setImmediate(resolve));
    expect(transport.stats.size).toBe(0);
    expect(delivered.length).toBeGreaterThan(0);
    // Drop-oldest keeps FIFO: the delivered frames stay in burst order.
    const deliveredCpu = delivered.map((e) => (e.body as { cpuPercent: number }).cpuPercent);
    expect(deliveredCpu.every((cpu, i) => i === 0 || cpu > deliveredCpu[i - 1]!)).toBe(true);
    // The burst overflowed the queue and said so.
    expect(transport.stats.dropped).toBeGreaterThan(0);
    expect(depthReports.length).toBeGreaterThan(0);

    // The link stayed up through the burst: the next frame still arrives.
    engine.send(envelopeOf("dev.log", logBody()));
    await vi.waitFor(() => expect(delivered.filter((e) => e.type === "dev.log")).toHaveLength(1), {
      timeout: 10000,
      interval: 5,
    });

    transport.close();
    await engine.close();
  });

  it("skips a malformed frame without tearing the link", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
    });
    const errors: Error[] = [];
    transport.on("error", (error) => errors.push(error));
    const metrics: DevMetricsSnapshot[] = [];
    transport.on("metrics", (snapshot) => metrics.push(snapshot));
    await transport.connect();

    engine.sendRaw(encodeFrame(Buffer.from("}}} not json {{{")));
    engine.send(envelopeOf("dev.metrics", metricsBody()));
    await vi.waitFor(() => expect(metrics.length).toBe(1), { timeout: 10000, interval: 5 });
    expect(errors[0]).toBeInstanceOf(EnvelopeDecodeError);
    expect(transport.stats.malformedFrames).toBe(1);
    expect(transport.state).toBe("ready");

    transport.close();
    await engine.close();
  });

  it("treats an oversize frame as a lost stream", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
    });
    const errors: Error[] = [];
    transport.on("error", (error) => errors.push(error));
    await transport.connect();

    const header = Buffer.alloc(4);
    header.writeUInt32LE(MAX_FRAME_BYTES + 1, 0);
    engine.sendRaw(header);
    await waitForState(transport, "closed");
    expect(errors[0]).toBeInstanceOf(OversizeFrameError);
    expect((errors[0] as OversizeFrameError).message).toContain("32 MiB");

    transport.close();
    await engine.close();
  });

  it("starts every connection with a fresh frame stream", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      backoff: { initialMs: 20, maxMs: 100 },
    });
    const metrics: DevMetricsSnapshot[] = [];
    transport.on("metrics", (snapshot) => metrics.push(snapshot));
    await transport.connect();

    // Half a header from the old link: two bytes that would poison the next
    // stream if the accumulator carried over. Let them land before the cut.
    const fresh = encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody())));
    engine.sendRaw(fresh.subarray(0, 2));
    await new Promise((resolve) => setTimeout(resolve, 50));

    // The engine restarts mid-frame; the next link's first full frame must
    // decode untouched by the stale two bytes.
    await engine.close();
    await waitForState(transport, "backoff");
    const returned = new FakeEngine(dir);
    await returned.listen();
    await waitForState(transport, "ready");

    returned.send(envelopeOf("dev.metrics", metricsBody()));
    await vi.waitFor(() => expect(metrics.length).toBe(1), { timeout: 10000, interval: 5 });
    expect(metrics[0]!.processId).toBe(process.pid); // a clean snapshot, not merged bytes
    expect(transport.stats.malformedFrames).toBe(0);

    transport.close();
    await returned.close();
  });

  it("counts a typed body that misses its shape without crashing the drain", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
    });
    const errors: Error[] = [];
    transport.on("error", (error) => errors.push(error));
    const logs: DevLogEntry[] = [];
    transport.on("log", (entry) => logs.push(entry));
    await transport.connect();

    // Valid JSON, valid envelope, body that no DevLogEntry could be.
    engine.send(envelopeOf("dev.log", { level: 42 }));
    engine.send(envelopeOf("dev.log", logBody()));
    await vi.waitFor(() => expect(logs.length).toBe(1), { timeout: 10000, interval: 5 });
    expect(errors.some((e) => e instanceof EnvelopeDecodeError)).toBe(true);
    expect(transport.stats.malformedFrames).toBe(1);
    expect(transport.state).toBe("ready"); // the drain survived its own callback

    transport.close();
    await engine.close();
  });

  it("never delivers queued frames after the link has died", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
      drainBatchSize: 1,
    });
    const delivered: IpcEnvelope[] = [];
    transport.on("envelope", (envelope) => delivered.push(envelope));
    await transport.connect();

    // A burst far wider than one frame per drain turn, then the link dies
    // mid-drain. Whatever is still queued dies with the link — frames that
    // surface after "closed" would have handlers mark a dead source "up".
    const burst = Buffer.concat(
      Array.from({ length: 400 }, () => encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody())))),
    );
    engine.sendRaw(burst);
    engine.dropClients();
    await engine.close();
    await waitForState(transport, "closed");

    const atClose = delivered.length;
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(delivered.length).toBe(atClose); // nothing surfaced after the death
    expect(transport.stats.size).toBe(0); // the queue went with the link

    transport.close();
  });

  it("keeps exactly one socket when a manual connect takes over mid-backoff", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      backoff: { initialMs: 30, maxMs: 120 },
    });
    await transport.connect();

    // The engine drops; the retry loop schedules its next attempt.
    await engine.close();
    await waitForState(transport, "backoff");

    // The endpoint returns and the shell connects by hand: the scheduled
    // retry must be cancelled, not stacked on top of this socket — two live
    // sockets would both feed the same accumulator, and close() would only
    // ever hang up the newest one.
    const returned = new FakeEngine(dir);
    await returned.listen();
    await transport.connect();
    await waitForState(transport, "ready");

    // Well past the old retry window: exactly one link ever existed here.
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(returned.connections).toBe(1);

    transport.close();
    await returned.close();
  });

  it("lets concurrent connect callers share the one in-flight attempt", async () => {
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
    });

    // Two callers enter before the dial answers: the second must ride the
    // same attempt, not be told the link is up while the first is pending.
    let secondStateAtResolve: LinkState | null = null;
    const first = transport.connect();
    const second = transport.connect().then(() => {
      secondStateAtResolve = transport.state;
    });
    expect(transport.state).toBe("connecting"); // the shared attempt is pending
    await Promise.all([first, second]);
    expect(secondStateAtResolve).toBe("ready"); // resolved when it truly was
    expect(transport.state).toBe("ready");

    transport.close();
    await engine.close();
  });

  it("joins a retry already dialing instead of opening a second socket", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      const dir = await newDir();
      const engine = new FakeEngine(dir);
      await engine.listen();
      const transport = new DevToolsTransport({
        endpoint: devtoolsEndpoint("linux", dir, "chorus"),
        backoff: { initialMs: 50, maxMs: 100 },
      });
      await transport.connect();
      const connectionsAtBaseline = engine.connections; // the first link

      // The client socket dies but the endpoint stays up: the link falls to
      // backoff and the loop schedules one retry.
      engine.dropClients();
      await waitForState(transport, "backoff");

      // Fire the retry synchronously: its dial is now in flight, unanswered.
      vi.advanceTimersByTime(60);

      // A shell attaches right here, mid-dial. It must join the attempt the
      // loop made — a second dial here would leave a socket that close()
      // never hangs up.
      let joinStateAtResolve: LinkState | null = null;
      const join = transport.connect().then(() => {
        joinStateAtResolve = transport.state;
      });
      expect(transport.state).toBe("connecting");

      // The dial answers on the real socket loop.
      vi.useRealTimers();
      await join;
      await new Promise((resolve) => setImmediate(resolve));
      expect(transport.state).toBe("ready");
      expect(joinStateAtResolve).toBe("ready");
      expect(engine.connections).toBe(connectionsAtBaseline + 1); // only the retry's dial — the join added none

      transport.close();
      await engine.close();
    } finally {
      vi.useRealTimers();
    }
  });

  it("runs one retry chain when error and close both report the dead link", async () => {
    // A failed dial fires 'error' AND 'close'. Each callback scheduling its
    // own retry would leak a parallel chain dialing beside the loop's timer
    // — twice the attempts, both feeding the same accumulator.
    const dir = await newDir(); // nothing listens here
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      backoff: { initialMs: 50, multiplier: 2, maxMs: 100 },
    });
    const errors: Error[] = [];
    transport.on("error", (error) => errors.push(error));
    await transport.connect().catch(() => {}); // initial dial fails at t≈0

    // Dead-endpoint window: retries land at 50, then +100 forever (the
    // schedule 50→100 caps at maxMs) — 7 failures by 600ms exactly.
    await new Promise((resolve) => setTimeout(resolve, 600));
    expect(errors.length).toBe(7);

    transport.close();
  });

  it("runs one socket when an error listener reconnects synchronously", async () => {
    // The failure handler used to emit 'error' before finalizing its retry:
    // a listener that reconnects from the error opened its socket, and the
    // half-finished handler then scheduled a retry of its own — which dials
    // again when it fires, leaving two live sockets feeding one accumulator.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      backoff: { initialMs: 50, maxMs: 100 },
    });
    await transport.connect();
    await waitForState(transport, "ready");
    const dialAtBaseline = engine.connections;

    let errorEmitted = false;
    transport.on("error", () => {
      errorEmitted = true;
      // Synchronous takeover: the listener recovers by hand.
      void transport.connect();
    });

    engine.dropClients();
    // The link is still ready when the drop lands — ride the failure down to
    // backoff before waiting for the takeover.
    await waitForState(transport, "backoff");
    await waitForState(transport, "ready");
    expect(errorEmitted).toBe(true);

    // Well past the retry the old handler would have scheduled: the takeover
    // is the link, so nothing may have dialed beside it.
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(engine.connections).toBe(dialAtBaseline + 1);

    // close() must hang up the one socket it owns — and only that one.
    const closedBefore = engine.closedClients;
    transport.close();
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(engine.closedClients).toBe(closedBefore + 1);
    expect(engine.connections).toBe(dialAtBaseline + 1);

    await engine.close();
  });

  it("keeps the transport closed when an error listener closes it", async () => {
    // The old ordering emitted the error first and only then moved the state
    // machine: a listener that closed from the error met the later backoff
    // transition and its retry timer, which overwrote the close.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      backoff: { initialMs: 50, maxMs: 100 },
    });
    await transport.connect();
    await waitForState(transport, "ready");

    let sawClosedFromListener = false;
    transport.on("error", () => {
      sawClosedFromListener = true;
      transport.close();
    });

    engine.dropClients();
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(sawClosedFromListener).toBe(true);
    expect(transport.state).toBe("closed");
    expect(engine.connections).toBe(1); // the takeover dial never happened

    await engine.close();
  });

  it("clears the dial's deadline when the dial fails or is cancelled", async () => {
    // A connect timer cleared only on success pinned a dead socket — and the
    // whole transport with it — past shutdown in a standalone consumer.
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      const deadPath = path.join("unused-dir", "chorus_devtools.sock");
      const options = {
        endpoint: { kind: "uds" as const, path: deadPath },
        autoReconnect: false,
        connectTimeoutMs: 5000,
      };

      // One dial that is closed before the endpoint can answer it.
      const cancelled = new DevToolsTransport(options);
      cancelled.connect().catch(() => {});
      cancelled.close();
      expect(vi.getTimerCount()).toBe(0);
      expect(cancelled.state).toBe("closed");

      // One dial that fails outright (autoReconnect off keeps the count clear
      // of a retry timer). The dial must leave no deadline behind.
      const failed = new DevToolsTransport(options);
      failed.connect().catch(() => {});
      // The dial's error comes off the real socket loop (only the timers are
      // faked here), so poll the real event loop until it lands.
      for (let turn = 0; turn < 20 && failed.state !== "closed"; turn++) {
        await new Promise<void>((resolve) => setImmediate(resolve));
      }
      expect(failed.state).toBe("closed");
      expect(vi.getTimerCount()).toBe(0);

      failed.close();
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it("derives the endpoint the engine binds per platform", () => {
    expect(devtoolsEndpoint("linux", "/tmp/chorus", "chorus")).toEqual({
      kind: "uds",
      path: path.join("/tmp/chorus", "chorus_devtools.sock"),
    });
    expect(devtoolsEndpoint("win32", "/tmp/chorus", "chorus")).toEqual({
      kind: "pipe",
      name: "chorus.devtools",
    });
    // The engine resolves empty settings to its own defaults.
    expect(devtoolsEndpoint("linux", "", "")).toEqual({
      kind: "uds",
      path: path.join("/tmp/chorus", "chorus_devtools.sock"),
    });
  });
});
