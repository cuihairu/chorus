// @chorus/devcore — IDE-agnostic core for the chorus devtools plane (IDE-EXT).
//
// Surfaces, all mirrored from the engine tree (no guessing):
//   frame      u32 LE length | payload, 32 MiB bound, zero-length legal
//              (src/Chorus.Messaging/Transports/LengthPrefixedStream.cs,
//               FrameReader.cs)
//   envelope   JSON camelCase { type, messageId, sessionId, createdTimeMs,
//              body, sourceEventId?, authorityEpoch? } — WhenWritingNull, so
//              unset optionals are absent, not null
//              (src/Chorus.Messaging/Envelope.cs, ProtocolJson.cs);
//              dev.log / dev.metrics bodies typed
//              (src/Chorus.Messaging/Ipc/IpcMessageTypes.cs,
//               src/Chorus.DevTools/DevToolsModels.cs)
//   transport  one push-only socket to the engine's optional devtools
//              channel (UDS on posix, named pipe on Windows), reconnect with
//              backoff, bounded drop-oldest delivery queue
//              (src/Chorus.DevTools/DevToolsService.cs)
//   attach     the session facade IDE shells consume
//
// The engine side is opt-in (--devtools; refused in the Production
// environment) and default-off: nothing binds without the flag.

export {
  FrameAccumulator,
  FRAME_HEADER,
  MAX_FRAME_BYTES,
  OversizeFrameError,
  encodeFrame,
} from "./frame.js";
export type { DecodedFrame } from "./frame.js";
export {
  DevLogType,
  DevMetricsType,
  DevToolsSession,
  EnvelopeDecodeError,
  asLogEntry,
  asMetricsSnapshot,
  decodeEnvelope,
} from "./envelope.js";
export type { DevLogEntry, DevMetricsSnapshot, IpcEnvelope } from "./envelope.js";
export { ReconnectBackoff } from "./backoff.js";
export type { BackoffOptions } from "./backoff.js";
export {
  DevToolsTransport,
  TransportError,
  devtoolsEndpoint,
} from "./transport.js";
export type {
  AttachEndpoint,
  DevToolsEvents,
  LinkState,
  QueueDepth,
  TransportOptions,
  TransportStats,
} from "./transport.js";
export { AttachSession } from "./attach.js";
export type { AttachEvents, AttachOptions, AttachStatus, SourceState } from "./attach.js";
