import * as asyncHooks from "node:async_hooks";
import * as diagnosticsChannel from "node:diagnostics_channel";
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
    await vi.waitFor(() => expect(engine.connections).toBe(1)); // the two callers rode one dial

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

  it("joins the one attempt when a connecting listener reconnects", async () => {
    // "connecting" used to fire before the attempt was registered: a state
    // listener reconnecting from the event saw no in-flight dial and opened
    // one, then the outer call overwrote the waiter and opened another.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
    });

    let reentered = false;
    let inner: Promise<void> | null = null;
    transport.on("state", (state) => {
      if (state !== "connecting" || reentered) return;
      reentered = true;
      inner = transport.connect();
    });

    const outer = transport.connect();
    await Promise.all([outer, inner!]); // both callers settle on the one attempt
    expect(transport.state).toBe("ready");

    // One dial, both callers resolved by it, and close() hangs up everything
    // that ever connected.
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(engine.connections).toBe(1);
    transport.close();
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(engine.closedClients).toBe(engine.connections);

    await engine.close();
  });

  it("stays closed when a connecting listener closes the transport", async () => {
    // The same window with close(): the old order let the listener tear down
    // an attempt that did not exist yet, and the outer call then dialed past
    // the close — the state machine ran connecting → closed → ready.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
    });

    let sawClosed = false;
    transport.on("state", (state) => {
      if (state !== "connecting") return;
      transport.close();
    });
    transport.on("state", (state) => {
      if (state === "closed") sawClosed = true;
    });

    await expect(transport.connect()).rejects.toThrow();
    expect(sawClosed).toBe(true);
    expect(transport.state).toBe("closed");

    // Nothing resurrects the link, and nothing that connected stays alive.
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(transport.state).toBe("closed");
    expect(engine.closedClients).toBe(engine.connections);

    await engine.close();
  });

  it("joins the one dial when a net diagnostics subscriber reconnects mid-attempt", async () => {
    // socket.connect() publishes the net.client.socket diagnostics channel
    // before it returns: a subscriber there reenters the dial's synchronous
    // segment — the state-event registration fix does not cover this
    // window. The subscriber's connect() must join the registered attempt;
    // a late registration let it open a second socket and orphaned the
    // outer waiter forever.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
    });

    const channel = diagnosticsChannel.channel("net.client.socket");
    let reentered = false;
    let inner: Promise<void> | null = null;
    const subscriber = () => {
      if (reentered) return;
      reentered = true;
      inner = transport.connect();
    };
    channel.subscribe(subscriber);
    try {
      const outer = transport.connect();
      await outer;
      expect(inner).not.toBeNull();
      await inner!;
      expect(transport.state).toBe("ready");
    } finally {
      channel.unsubscribe(subscriber);
    }

    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(engine.connections).toBe(1); // the mid-dial caller joined, not dialed
    transport.close();
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(engine.closedClients).toBe(engine.connections);

    await engine.close();
  });

  it("stays closed when a net diagnostics subscriber closes the transport mid-attempt", async () => {
    // The same synchronous window with close(): registration used to land
    // after the subscriber ran, so the close could not reach the dial — the
    // socket was registered afterwards and lived past the closed state,
    // with its deadline timer pinned behind it.
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      const dir = await newDir();
      const engine = new FakeEngine(dir);
      await engine.listen();
      const transport = new DevToolsTransport({
        endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      });

      const channel = diagnosticsChannel.channel("net.client.socket");
      let closed = false;
      const subscriber = () => {
        if (closed) return;
        closed = true;
        transport.close();
      };
      channel.subscribe(subscriber);
      let rejection: Error | null = null;
      try {
        await transport.connect().catch((error: Error) => {
          rejection = error;
        });
      } finally {
        channel.unsubscribe(subscriber);
      }
      expect(rejection).not.toBeNull(); // the caller is told, not left pending
      expect(transport.state).toBe("closed");
      // Fake-timer advances do not run the real loop; setImmediate pumps let
      // the socket events the dial started finish arriving.
      for (let turn = 0; turn < 20; turn += 1) {
        await new Promise((resolve) => setImmediate(resolve));
      }
      // Node's dial may still reach the engine before the repeated teardown
      // lands — what must hold is that nothing stays connected and no new
      // dial ever happens.
      const dialed = engine.connections;
      expect(dialed).toBeLessThanOrEqual(1);
      expect(vi.getTimerCount()).toBe(0); // the deadline died with the dial

      // The closed transport never dials again.
      await vi.advanceTimersByTimeAsync(8000);
      for (let turn = 0; turn < 20; turn += 1) {
        await new Promise((resolve) => setImmediate(resolve));
      }
      expect(engine.connections).toBe(dialed);
      expect(engine.closedClients).toBe(engine.connections); // no live line past the close

      await engine.close();
    } finally {
      vi.useRealTimers();
    }
  });

  it("joins the one attempt when a close window's hook reconnects", async () => {
    // A hook inside the attempt's own Promise construction calls connect():
    // with no promise registered yet, the reentrant call used to build a
    // second attempt that the outer one overwrote — two dials, and the
    // inner waiter stranded when the outer settled.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
    });

    let hooked = false;
    let inner: Promise<void> | null = null;
    const hook = asyncHooks
      .createHook({
        init(_id, type) {
          if (type !== "PROMISE" || hooked) return;
          hooked = true;
          hook.disable();
          inner = transport.connect();
        },
      })
      .enable();

    const outer = transport.connect();
    try {
      await Promise.all([outer, inner!]); // both callers settle on the one dial
    } finally {
      hook.disable();
    }
    expect(hooked).toBe(true); // the reentrancy really did land in the window
    expect(inner).not.toBe(outer); // different promises
    expect(transport.state).toBe("ready");
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(engine.connections).toBe(1); // one dial, not two
    transport.close();
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(engine.closedClients).toBe(engine.connections);

    await engine.close();
  });

  it("joins the one attempt through both reentrancy windows", async () => {
    // A hook reconnects from inside the attempt's own Promise construction
    // and again from inside the joiner bindings that construction ends
    // with: the second call lands where the claim is already released but
    // the waiter and the shared promise are registered — it must join that
    // attempt instead of dialing a second one. Joiner promises are created
    // inside the hook body, so only out-of-hook promise inits count as
    // phases.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
    });

    let inHook = false;
    let phase = 0;
    let inner: Promise<void> | null = null;
    let inner2: Promise<void> | null = null;
    const hook = asyncHooks
      .createHook({
        init(_id, type) {
          if (type !== "PROMISE" || inHook || phase >= 2) return;
          phase += 1;
          inHook = true;
          try {
            if (phase === 1) inner = transport.connect();
            else inner2 = transport.connect();
          } finally {
            inHook = false;
          }
        },
      })
      .enable();

    const outer = transport.connect();
    try {
      await Promise.all([outer, inner!, inner2!]); // every caller settles on the one dial
    } finally {
      hook.disable();
    }
    expect(phase).toBe(2); // both reentrancy windows really did land
    expect(transport.state).toBe("ready");
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(engine.connections).toBe(1); // one dial, not two
    transport.close();
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(engine.closedClients).toBe(engine.connections);

    await engine.close();
  });

  it("leaves no retry behind when a close lands in the retry timer's own creation", async () => {
    // The reconnect timer's async_hooks.init fires before
    // `this.reconnectTimer = ...` writes its value: a close there cleared
    // nothing, and the assignment left a referenced retry behind — with the
    // backoff state published over the closed one. Real timers, so the
    // retry never fires during the test and the init hook can fire at all.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      backoff: { initialMs: 8000, maxMs: 8000 },
    });
    await transport.connect();

    const states: LinkState[] = [];
    transport.on("state", (state) => states.push(state));
    const transportState = transport as unknown as { reconnectTimer: unknown; socket: unknown };
    let hooked = false;
    let sawNoTimer = false;
    const hook = asyncHooks
      .createHook({
        init(_id, type) {
          if (type !== "Timeout" || hooked) return;
          // The retry timer is the one made while the link is already torn
          // down: pre-failure timers (the drain scheduler's, waitFor's
          // own) would close the transport before any failure existed.
          if (transportState.socket !== null) return;
          hooked = true;
          hook.disable();
          sawNoTimer = transportState.reconnectTimer === null;
          transport.close();
        },
      })
      .enable();

    try {
      engine.dropClients();
      await new Promise((resolve) => setTimeout(resolve, 50));
    } finally {
      hook.disable();
    }
    expect(hooked).toBe(true); // the close really did land in the timer's creation
    expect(sawNoTimer).toBe(true); // ... before the retry was written in
    expect(transport.state).toBe("closed");
    expect(states).not.toContain("backoff"); // closed is not overwritten
    expect(transportState.reconnectTimer).toBeNull();

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(engine.connections).toBe(1); // the closed transport never re-dials

    await engine.close();
  });

  it("keeps the transport closed when a close lands in the attempt's own construction", async () => {
    // The promise's async_hooks.init fires before its executor body runs: a
    // subscriber closing there met no waiter, no pending promise and no
    // socket, so it cleared nothing — and the outer call went on to register
    // them all and announce connecting over closed.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
    });

    const states: LinkState[] = [];
    transport.on("state", (state) => states.push(state));
    const transportState = transport as unknown as { pendingConnect: unknown };
    let hooked = false;
    let sawUnregistered = false;
    const hook = asyncHooks
      .createHook({
        init(_id, type) {
          if (type !== "PROMISE" || hooked) return;
          hooked = true;
          hook.disable();
          sawUnregistered = transportState.pendingConnect === null;
          transport.close();
        },
      })
      .enable();

    let rejection: Error | null = null;
    try {
      await transport.connect().catch((error: Error) => {
        rejection = error;
      });
    } finally {
      hook.disable();
    }
    expect(hooked).toBe(true); // the close did land in the registration window
    expect(sawUnregistered).toBe(true); // ... before the attempt was registered
    expect(rejection).not.toBeNull(); // the caller is told, not left pending
    expect(transport.state).toBe("closed");
    expect(states).not.toContain("connecting"); // no transition past the close
    for (let turn = 0; turn < 20; turn += 1) {
      await new Promise((resolve) => setImmediate(resolve));
    }
    expect(engine.connections).toBe(0); // the cancelled dial never happened

    await engine.close();
  });

  it("leaves no deadline behind when a close lands in the dial timer's own creation", async () => {
    // setTimeout's async_hooks.init fires before `this.connectTimer = ...`
    // writes its value: a close there cleared nothing, and the assignment put
    // the timer back into the field — pinning the transport past shutdown.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      connectTimeoutMs: 5000,
    });

    const transportState = transport as unknown as { connectTimer: unknown };
    let hooked = false;
    let sawNoTimer = false;
    const hook = asyncHooks
      .createHook({
        init(_id, type) {
          if (type !== "Timeout" || hooked) return;
          hooked = true;
          hook.disable();
          sawNoTimer = transportState.connectTimer === null;
          transport.close();
        },
      })
      .enable();

    let rejection: Error | null = null;
    try {
      await transport.connect().catch((error: Error) => {
        rejection = error;
      });
    } finally {
      hook.disable();
    }
    expect(hooked).toBe(true); // the close did land in the timer's creation
    expect(sawNoTimer).toBe(true); // ... before the deadline was written in
    expect(rejection).not.toBeNull();
    expect(transport.state).toBe("closed");
    expect(transportState.connectTimer).toBeNull();
    for (let turn = 0; turn < 20; turn += 1) {
      await new Promise((resolve) => setImmediate(resolve));
    }
    expect(engine.connections).toBe(0); // the closed transport never dialed

    await engine.close();
  });

  it("stops the receiving stack when an error listener closes the transport", async () => {
    // One chunk carrying a malformed frame and a valid metrics frame: the
    // error report used to return and onData kept reading the rest of the
    // chunk — a closed transport's queue re-seeded with fresh frames.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
    });

    const metrics: DevMetricsSnapshot[] = [];
    transport.on("metrics", (snapshot) => metrics.push(snapshot));
    let closed = false;
    transport.on("error", () => {
      if (closed) return;
      closed = true;
      transport.close();
    });

    await transport.connect();
    const chunk = Buffer.concat([
      encodeFrame(Buffer.from("}}} not json {{{")),
      encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody()))),
    ]);
    engine.sendRaw(chunk);

    await vi.waitFor(() => expect(closed).toBe(true));
    await new Promise((resolve) => setImmediate(resolve));
    expect(metrics).toHaveLength(0); // the frame behind the close never delivered
    expect(transport.stats.size).toBe(0);
    expect(transport.state).toBe("closed");

    await engine.close();
  });

  it("drops the in-flight frame when a backpressure listener closes the transport", async () => {
    // The overflow report used to return and enqueue pushed the current
    // frame anyway — a queue refilled behind a closed transport.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
      queueCapacity: 1,
    });

    let closed = false;
    transport.on("backpressure", () => {
      closed = true;
      transport.close();
    });
    let deliveriesAfterClose = 0;
    transport.on("envelope", () => {
      if (closed) deliveriesAfterClose += 1;
    });

    await transport.connect();
    // A third frame behind the close: the old receiving loop read on through
    // it after the overflow report returned.
    const chunk = Buffer.concat([
      encodeFrame(JSON.stringify(envelopeOf("dev.log", logBody()))),
      encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody()))),
      encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody()))),
    ]);
    engine.sendRaw(chunk);

    await vi.waitFor(() => expect(closed).toBe(true));
    await new Promise((resolve) => setImmediate(resolve));
    expect(transport.stats.dropped).toBe(1);
    expect(transport.stats.size).toBe(0); // the frame behind the close is not re-seeded
    expect(deliveriesAfterClose).toBe(0); // and nothing from it ever delivers
    expect(transport.state).toBe("closed");

    await engine.close();
  });

  it("reports but does not re-publish when a metrics listener closes and throws", async () => {
    // A typed delivery that closes and then throws used to land in the decode
    // handler: a good frame counted as malformed and an error republished
    // behind the closed transport.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
    });

    const metrics: DevMetricsSnapshot[] = [];
    const errors: Error[] = [];
    let closed = false;
    transport.on("metrics", () => {
      metrics.push(metricsBody() as unknown as DevMetricsSnapshot);
      if (closed) return;
      closed = true;
      transport.close();
      throw new Error("consumer threw after closing");
    });
    transport.on("error", (error) => errors.push(error));

    await transport.connect();
    const chunk = Buffer.concat([
      encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody()))),
      encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody()))),
    ]);
    engine.sendRaw(chunk);

    await vi.waitFor(() => expect(transport.state).toBe("closed"));
    await new Promise((resolve) => setImmediate(resolve));
    expect(metrics).toHaveLength(1); // the close lands on the first snapshot
    expect(transport.stats.malformedFrames).toBe(0); // a good frame is not bad
    expect(errors).toHaveLength(0); // nothing republished behind the close

    await engine.close();
  });

  it("stops the drain when an envelope listener closes the transport", async () => {
    // Delivering the first frame used to return and the typed emits for
    // that same frame fired behind the close — a closed transport pushing
    // metrics.
    const dir = await newDir();
    const engine = new FakeEngine(dir);
    await engine.listen();
    const transport = new DevToolsTransport({
      endpoint: devtoolsEndpoint("linux", dir, "chorus"),
      autoReconnect: false,
    });

    const metrics: DevMetricsSnapshot[] = [];
    const envelopes: IpcEnvelope[] = [];
    transport.on("envelope", (envelope) => {
      envelopes.push(envelope);
      if (envelopes.length === 1) transport.close();
    });
    transport.on("metrics", (snapshot) => metrics.push(snapshot));

    await transport.connect();
    const chunk = Buffer.concat([
      encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody()))),
      encodeFrame(JSON.stringify(envelopeOf("dev.metrics", metricsBody()))),
    ]);
    engine.sendRaw(chunk);

    await vi.waitFor(() => expect(transport.state).toBe("closed"));
    await new Promise((resolve) => setImmediate(resolve));
    expect(metrics).toHaveLength(0); // no typed delivery after closed
    expect(envelopes).toHaveLength(1); // the close lands on the first frame
    expect(transport.stats.size).toBe(0);

    await engine.close();
  });

  it("leaves no retry timer when a backoff listener closes the transport", async () => {
    // "backoff" used to fire before the retry timer was stored: a state
    // listener closing from the event could not clear a timer that did not
    // exist yet, and the failure path created it afterwards — a referenced
    // timer that pinned the transport past shutdown.
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      const dir = await newDir();
      const engine = new FakeEngine(dir);
      await engine.listen();
      const transport = new DevToolsTransport({
        endpoint: devtoolsEndpoint("linux", dir, "chorus"),
        backoff: { initialMs: 8000, maxMs: 8000 },
      });
      await transport.connect();

      transport.on("state", (state) => {
        if (state === "backoff") transport.close();
      });
      engine.dropClients();
      await vi.waitFor(() => expect(transport.state).toBe("closed"));
      expect(vi.getTimerCount()).toBe(0);

      // The closed transport never dials again.
      await vi.advanceTimersByTimeAsync(8000);
      expect(engine.connections).toBe(1);

      await engine.close();
    } finally {
      vi.useRealTimers();
    }
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
