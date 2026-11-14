import { describe, expect, it } from "vitest";
import {
  asLogEntry,
  asMetricsSnapshot,
  decodeEnvelope,
  DevToolsSession,
  EnvelopeDecodeError,
  IpcEnvelope,
} from "./envelope.js";
import { encodeFrame } from "./frame.js";

// The exact JSON shape the engine puts on the wire (captured from a real
// EngineHost --devtools attach): camelCase keys, unset optionals absent.
const metricsJson = `{"type":"dev.metrics","messageId":"m_180349b9e1a64392a857156d984f2cd5","sessionId":"${DevToolsSession}","createdTimeMs":1794633803393,"body":{"cpuPercent":0.69,"managedMemoryBytes":3051736,"workingSetBytes":88104960,"threadCount":34,"processId":2900127,"timestampMs":1794633803393}}`;
const logJson = `{"type":"dev.log","messageId":"m_abc","sessionId":"${DevToolsSession}","createdTimeMs":1794633803500,"body":{"level":"Information","category":"Microsoft.Hosting.Lifetime","timestampMs":1794633803499,"body":"Now listening on: http://127.0.0.1:18773"}}`;

describe("envelope", () => {
  it("decodes a real dev.metrics frame into a typed snapshot", () => {
    const envelope = decodeEnvelope(encodeFrame(metricsJson).subarray(4));
    expect(envelope.type).toBe("dev.metrics");
    expect(envelope.sessionId).toBe(DevToolsSession);
    expect(envelope.messageId).toMatch(/^m_[0-9a-f]{32}$/);
    expect(envelope.createdTimeMs).toBe(1794633803393);
    const snapshot = asMetricsSnapshot(envelope)!;
    expect(snapshot.cpuPercent).toBeCloseTo(0.69);
    expect(snapshot.managedMemoryBytes).toBe(3051736);
    expect(snapshot.processId).toBe(2900127);
  });

  it("decodes a real dev.log frame into a typed entry", () => {
    const entry = asLogEntry(decodeEnvelope(logJson))!;
    expect(entry.level).toBe("Information");
    expect(entry.category).toBe("Microsoft.Hosting.Lifetime");
    expect(entry.body).toContain("Now listening");
    expect(entry.timestampMs).toBe(1794633803499);
  });

  it("keeps absent optionals absent and carries them when present", () => {
    const bare = decodeEnvelope(logJson);
    expect("sourceEventId" in bare).toBe(false);
    expect("authorityEpoch" in bare).toBe(false);

    const stamped: IpcEnvelope = decodeEnvelope(
      `{"type":"engine.snapshot","messageId":"m_1","sessionId":"s","createdTimeMs":1,"body":{},"sourceEventId":"evt-9","authorityEpoch":3}`,
    );
    expect(stamped.sourceEventId).toBe("evt-9");
    expect(stamped.authorityEpoch).toBe(3);
  });

  it("treats a null cpuPercent as an absent key, per WhenWritingNull", () => {
    const noCpu = `{"type":"dev.metrics","messageId":"m_2","sessionId":"${DevToolsSession}","createdTimeMs":1,"body":{"managedMemoryBytes":1,"workingSetBytes":2,"threadCount":3,"processId":4,"timestampMs":5}}`;
    const snapshot = asMetricsSnapshot(decodeEnvelope(noCpu))!;
    expect(snapshot.cpuPercent).toBeUndefined();
  });

  it("throws EnvelopeDecodeError on garbage and on shape misses", () => {
    expect(() => decodeEnvelope(Buffer.from("not json at all"))).toThrow(EnvelopeDecodeError);
    expect(() => decodeEnvelope(`{"type":"","messageId":"m"}`)).toThrow(EnvelopeDecodeError);
    expect(() => decodeEnvelope(`[1,2,3]`)).toThrow(EnvelopeDecodeError);
    const wrongBody = decodeEnvelope(
      `{"type":"dev.log","messageId":"m","sessionId":"s","createdTimeMs":1,"body":{"level":42}}`,
    );
    expect(() => asLogEntry(wrongBody)).toThrow(EnvelopeDecodeError);
    const noProcess = decodeEnvelope(
      `{"type":"dev.metrics","messageId":"m","sessionId":"s","createdTimeMs":1,"body":{"managedMemoryBytes":1}}`,
    );
    expect(() => asMetricsSnapshot(noProcess)).toThrow(EnvelopeDecodeError);
  });

  it("narrowing helpers answer null for foreign message types", () => {
    const heartbeat = decodeEnvelope(
      `{"type":"gateway.heartbeat","messageId":"m","sessionId":"__heartbeat","createdTimeMs":1,"body":{}}`,
    );
    expect(asLogEntry(heartbeat)).toBeNull();
    expect(asMetricsSnapshot(heartbeat)).toBeNull();
  });
});
