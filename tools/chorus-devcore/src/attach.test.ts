import * as fs from "node:fs/promises";
import * as net from "node:net";
import * as os from "node:os";
import * as path from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { encodeFrame } from "./frame.js";
import { DevLogEntry, DevMetricsSnapshot, DevToolsSession } from "./envelope.js";
import { AttachSession, AttachStatus } from "./attach.js";

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

  close(): Promise<void> {
    for (const client of this.clients) client.destroy();
    this.clients = [];
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

function envelopeOf(type: string, body: Record<string, unknown>): Record<string, unknown> {
  return {
    type,
    messageId: `m_${Math.random().toString(16).slice(2).padStart(12, "0")}`,
    sessionId: DevToolsSession,
    createdTimeMs: Date.now(),
    body,
  };
}

describe("attach session", () => {
  it("walks the sources from off through waiting to up as frames land", async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), "devcore-"));
    dirs.push(dir);
    const engine = new FakeEngine(dir);
    await engine.listen();
    const session = new AttachSession({
      transport: { endpoint: AttachSession.endpoint("linux", dir, "chorus"), autoReconnect: false },
    });

    const statuses: AttachStatus[] = [];
    session.on("source-state", (status) => statuses.push(status));
    const metrics: DevMetricsSnapshot[] = [];
    const logs: DevLogEntry[] = [];
    session.on("metrics", (snapshot) => metrics.push(snapshot));
    session.on("log", (entry) => logs.push(entry));

    expect(session.status).toEqual({ link: "idle", metrics: "off", log: "off" });
    await session.attach();
    // Attached, nothing yet: the shells show waiting, not a dead panel.
    expect(session.status).toEqual({ link: "ready", metrics: "waiting", log: "waiting" });

    engine.send(
      envelopeOf("dev.metrics", {
        cpuPercent: 3.5,
        managedMemoryBytes: 1,
        workingSetBytes: 2,
        threadCount: 3,
        processId: 4,
        timestampMs: 5,
      }),
    );
    engine.send(envelopeOf("dev.log", { level: "Warning", category: "c", timestampMs: 1, body: "b" }));
    await vi.waitFor(() => expect(metrics.length).toBe(1), { timeout: 10000, interval: 5 });
    expect(logs).toHaveLength(1);
    expect(session.status).toEqual({ link: "ready", metrics: "up", log: "up" });
    expect(session.lastMetrics!.cpuPercent).toBeCloseTo(3.5);
    expect(session.lastLog!.body).toBe("b");
    expect(statuses.some((s) => s.metrics === "up" && s.log === "waiting")).toBe(true);

    session.detach();
    expect(session.status).toEqual({ link: "closed", metrics: "off", log: "off" });
    await engine.close();
  });

  it("falls back to waiting while the link is down and recovers", async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), "devcore-"));
    dirs.push(dir);
    const engine = new FakeEngine(dir);
    await engine.listen();
    const session = new AttachSession({
      transport: { endpoint: AttachSession.endpoint("linux", dir, "chorus"), backoff: { initialMs: 20, maxMs: 100 } },
    });
    await session.attach();

    engine.send(
      envelopeOf("dev.metrics", {
        cpuPercent: 1,
        managedMemoryBytes: 1,
        workingSetBytes: 2,
        threadCount: 3,
        processId: 4,
        timestampMs: 5,
      }),
    );
    await vi.waitFor(() => expect(session.status.metrics).toBe("up"), { timeout: 10000, interval: 5 });

    // The engine restarts: sources fall back to waiting, not off.
    engine.close();
    await vi.waitFor(() => expect(session.status.link).toBe("backoff"), { timeout: 10000, interval: 5 });
    expect(session.status.metrics).toBe("waiting");

    // The engine returns on the same path: the session recovers by itself.
    const returned = new FakeEngine(dir);
    await returned.listen();
    await vi.waitFor(() => expect(session.status.link).toBe("ready"), { timeout: 10000, interval: 5 });
    expect(session.status.metrics).toBe("waiting"); // nothing since the restart

    const metrics: DevMetricsSnapshot[] = [];
    session.on("metrics", (snapshot) => metrics.push(snapshot));
    returned.send(
      envelopeOf("dev.metrics", {
        cpuPercent: 2,
        managedMemoryBytes: 1,
        workingSetBytes: 2,
        threadCount: 3,
        processId: 4,
        timestampMs: 5,
      }),
    );
    await vi.waitFor(() => expect(metrics.length).toBe(1), { timeout: 10000, interval: 5 });
    expect(session.status.metrics).toBe("up");

    session.detach();
    await returned.close();
  });

  it("forwards backpressure and errors from the transport", async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), "devcore-"));
    dirs.push(dir);
    const engine = new FakeEngine(dir);
    await engine.listen();
    const session = new AttachSession({
      transport: { endpoint: AttachSession.endpoint("linux", dir, "chorus"), autoReconnect: false, queueCapacity: 2 },
    });
    const backpressure: number[] = [];
    const errors: Error[] = [];
    const envelopes: string[] = [];
    session.on("backpressure", (depth) => backpressure.push(depth.dropped));
    session.on("error", (error) => errors.push(error));
    session.on("log", (entry) => envelopes.push(entry.body));
    await session.attach();

    // Four log frames against a capacity of two: two dropped, two delivered.
    for (const body of ["one", "two", "three", "four"]) {
      engine.send(envelopeOf("dev.log", { level: "Information", category: "c", timestampMs: 1, body }));
    }
    await vi.waitFor(() => expect(envelopes.length).toBe(2), { timeout: 10000, interval: 5 });
    expect(envelopes).toEqual(["three", "four"]);
    expect(backpressure.length).toBeGreaterThan(0);
    expect(backpressure[0]).toBeGreaterThan(0);

    session.detach();
    await engine.close();
  });
});
