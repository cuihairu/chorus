// The IPC envelope (src/Chorus.Messaging/Envelope.cs) as it rides the wire.
// The engine serializes with ProtocolJson.SerializerOptions —
// JsonSerializerDefaults.Web + explicit camelCase + WhenWritingNull
// (src/Chorus.Protocol/ProtocolJson.cs) — so sourceEventId and authorityEpoch
// are absent from the JSON, not null, when unset, and body is the JSON
// element of the message payload.
//
// The devtools plane adds two message types
// (src/Chorus.Messaging/Ipc/IpcMessageTypes.cs): dev.log forwards one ILogger
// entry (DevToolsLoggerProvider), dev.metrics pushes one process snapshot
// (DevToolsService), both carrying the "__devtools" sentinel session id
// (the IPC heartbeat's "__heartbeat" is the same device).

export interface IpcEnvelope {
  type: string;
  /** One delivery: "m_" + 32 hex chars (EnvelopeFactory). */
  messageId: string;
  sessionId: string;
  createdTimeMs: number;
  body: unknown;
  sourceEventId?: string;
  authorityEpoch?: number;
}

export const DevLogType = "dev.log";
export const DevMetricsType = "dev.metrics";
export const DevToolsSession = "__devtools";

/** One forwarded ILogger entry (DevToolsLogEntry in DevToolsModels.cs). */
export interface DevLogEntry {
  /** LogLevel member name: "Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical". */
  level: string;
  category: string;
  timestampMs: number;
  /** Formatter output; the exception (if any) rides beneath it. */
  body: string;
}

/** One process snapshot (DevMetricsSnapshot in DevToolsModels.cs). */
export interface DevMetricsSnapshot {
  /** Present once the host's sampler has a first window (ProcessCpuGauge); the
   * key is omitted entirely while null (WhenWritingNull). */
  cpuPercent?: number;
  managedMemoryBytes: number;
  workingSetBytes: number;
  threadCount: number;
  processId: number;
  timestampMs: number;
}

export class EnvelopeDecodeError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "EnvelopeDecodeError";
  }
}

function asRecord(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new EnvelopeDecodeError("envelope body is not a JSON object");
  }
  return value as Record<string, unknown>;
}

/** Decodes one frame payload into an envelope; throws EnvelopeDecodeError on
 * a payload that is not the engine's envelope shape. Frame alignment is
 * unaffected — callers may skip a malformed frame and keep reading. */
export function decodeEnvelope(payload: Buffer | string): IpcEnvelope {
  let parsed: unknown;
  try {
    parsed = JSON.parse(typeof payload === "string" ? payload : payload.toString("utf8"));
  } catch (error) {
    throw new EnvelopeDecodeError(`envelope is not JSON: ${(error as Error).message}`);
  }
  const record = asRecord(parsed);
  if (typeof record.type !== "string" || record.type.length === 0) {
    throw new EnvelopeDecodeError("envelope has no type");
  }
  const envelope: IpcEnvelope = {
    type: record.type,
    messageId: typeof record.messageId === "string" ? record.messageId : "",
    sessionId: typeof record.sessionId === "string" ? record.sessionId : "",
    createdTimeMs: typeof record.createdTimeMs === "number" ? record.createdTimeMs : 0,
    body: record.body,
  };
  if (typeof record.sourceEventId === "string") envelope.sourceEventId = record.sourceEventId;
  if (typeof record.authorityEpoch === "number") envelope.authorityEpoch = record.authorityEpoch;
  return envelope;
}

/** Narrows to a typed body, or null when this envelope is not one. */
export function asLogEntry(envelope: IpcEnvelope): DevLogEntry | null {
  if (envelope.type !== DevLogType) return null;
  const body = asRecord(envelope.body);
  if (
    typeof body.level !== "string" ||
    typeof body.category !== "string" ||
    typeof body.timestampMs !== "number" ||
    typeof body.body !== "string"
  ) {
    throw new EnvelopeDecodeError(`${DevLogType} body does not match DevLogEntry`);
  }
  return {
    level: body.level,
    category: body.category,
    timestampMs: body.timestampMs,
    body: body.body,
  };
}

/** Narrows to a typed body, or null when this envelope is not one. */
export function asMetricsSnapshot(envelope: IpcEnvelope): DevMetricsSnapshot | null {
  if (envelope.type !== DevMetricsType) return null;
  const body = asRecord(envelope.body);
  const number = (key: string): number => {
    const value = body[key];
    if (typeof value !== "number") throw new EnvelopeDecodeError(`${DevMetricsType} body lacks ${key}`);
    return value;
  };
  const snapshot: DevMetricsSnapshot = {
    managedMemoryBytes: number("managedMemoryBytes"),
    workingSetBytes: number("workingSetBytes"),
    threadCount: number("threadCount"),
    processId: number("processId"),
    timestampMs: number("timestampMs"),
  };
  if (typeof body.cpuPercent === "number") snapshot.cpuPercent = body.cpuPercent;
  return snapshot;
}
