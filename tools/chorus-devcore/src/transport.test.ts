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
    client.on("error", () => {});
  });
  private clients: net.Socket[] = [];
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
