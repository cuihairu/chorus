// Wire framing for the chorus IPC/devtools plane, mirrored from the engine
// (src/Chorus.Messaging/Transports/LengthPrefixedStream.cs and
// FrameReader.cs): a frame is a 4-byte LITTLE-endian payload length followed
// by exactly that many bytes; one frame never exceeds 32 MiB
// (FrameReader.MaxFrameBytes) and a zero-length payload is legal on the wire.

export const MAX_FRAME_BYTES = 32 * 1024 * 1024;
export const FRAME_HEADER = 4;

export class OversizeFrameError extends Error {
  constructor(readonly length: number) {
    super(`frame length ${length} exceeds the 32 MiB bound`);
    this.name = "OversizeFrameError";
  }
}

/** Encodes one wire frame: u32 LE length | payload. */
export function encodeFrame(payload: Uint8Array | string): Buffer {
  const body = typeof payload === "string" ? Buffer.from(payload, "utf8") : Buffer.from(payload);
  if (body.length > MAX_FRAME_BYTES) throw new OversizeFrameError(body.length);
  const out = Buffer.alloc(FRAME_HEADER + body.length);
  out.writeUInt32LE(body.length, 0);
  body.copy(out, FRAME_HEADER);
  return out;
}

export type DecodedFrame = { readonly payload: Buffer; readonly length: number };

/**
 * Incremental decoder for a byte stream of frames, the read side of
 * FrameReader. Feed it arbitrary chunks (a single read may straddle any
 * number of frames or split a header); every call returns the frames that
 * became complete, in order. An oversize length means the stream is no
 * longer frame-aligned: the accumulator must be discarded and the link
 * treated as failed — the engine rejects such a connection the same way.
 */
export class FrameAccumulator {
  private pending: Buffer = Buffer.alloc(0);

  /** Returns the frames completed by this chunk, in arrival order. */
  push(chunk: Buffer): DecodedFrame[] {
    this.pending = this.pending.length === 0 ? chunk : Buffer.concat([this.pending, chunk]);
    const frames: DecodedFrame[] = [];
    for (;;) {
      if (this.pending.length < FRAME_HEADER) break;
      const length = this.pending.readUInt32LE(0);
      if (length > MAX_FRAME_BYTES) {
        this.pending = Buffer.alloc(0);
        throw new OversizeFrameError(length);
      }
      if (this.pending.length < FRAME_HEADER + length) break;
      const payload = Buffer.from(this.pending.subarray(FRAME_HEADER, FRAME_HEADER + length));
      this.pending = this.pending.subarray(FRAME_HEADER + length);
      frames.push({ payload, length });
    }
    return frames;
  }

  /** Bytes buffered so far that do not yet form a complete frame. */
  get buffered(): number {
    return this.pending.length;
  }
}
