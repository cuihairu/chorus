import { describe, expect, it } from "vitest";
import {
  FrameAccumulator,
  FRAME_HEADER,
  MAX_FRAME_BYTES,
  OversizeFrameError,
  encodeFrame,
} from "./frame.js";

describe("frame", () => {
  it("encodes the 4-byte little-endian length before the payload", () => {
    const frame = encodeFrame("hi");
    expect(frame.length).toBe(FRAME_HEADER + 2);
    expect(frame.readUInt32LE(0)).toBe(2);
    expect(frame.subarray(FRAME_HEADER).toString("utf8")).toBe("hi");
  });

  it("round-trips a payload through the accumulator", () => {
    const accumulator = new FrameAccumulator();
    const frames = accumulator.push(encodeFrame("hello world"));
    expect(frames).toHaveLength(1);
    expect(frames[0]!.payload.toString("utf8")).toBe("hello world");
    expect(accumulator.buffered).toBe(0);
  });

  it("reassembles frames from straddled and split chunks", () => {
    const accumulator = new FrameAccumulator();
    const wire = Buffer.concat([encodeFrame("alpha"), encodeFrame("be"), encodeFrame("gamma")]);

    // Byte-by-byte: every frame completes exactly when its last byte lands.
    let seen: string[] = [];
    for (const byte of wire) {
      for (const frame of accumulator.push(Buffer.from([byte]))) {
        seen.push(frame.payload.toString("utf8"));
      }
    }
    expect(seen).toEqual(["alpha", "be", "gamma"]);
    expect(accumulator.buffered).toBe(0);

    // Header split across two chunks.
    const whole = encodeFrame("delta");
    seen = [];
    seen.push(...accumulator.push(whole.subarray(0, 2)).map((f) => f.payload.toString()));
    expect(accumulator.buffered).toBe(2); // the two header bytes wait for the rest
    seen.push(...accumulator.push(whole.subarray(2)).map((f) => f.payload.toString()));
    expect(seen).toEqual(["delta"]);
    expect(accumulator.buffered).toBe(0);
  });

  it("accepts a zero-length frame as legal wire traffic", () => {
    const zero = Buffer.alloc(FRAME_HEADER);
    zero.writeUInt32LE(0, 0);
    const frames = new FrameAccumulator().push(zero);
    expect(frames).toHaveLength(1);
    expect(frames[0]!.payload.length).toBe(0);
  });

  it("rejects an oversize length like the engine does", () => {
    const header = Buffer.alloc(FRAME_HEADER);
    header.writeUInt32LE(MAX_FRAME_BYTES + 1, 0);
    const accumulator = new FrameAccumulator();
    expect(() => accumulator.push(header)).toThrow(OversizeFrameError);
    // After a framing failure the accumulator resets itself; the caller is
    // still expected to discard it — the stream can no longer be trusted,
    // a fresh one starts on a fresh link.
    expect(accumulator.buffered).toBe(0);
  });

  it("keeps a frame just under the bound", () => {
    const payload = Buffer.alloc(MAX_FRAME_BYTES, 7);
    const frame = encodeFrame(payload);
    expect(frame.readUInt32LE(0)).toBe(MAX_FRAME_BYTES);
    const frames = new FrameAccumulator().push(frame);
    expect(frames).toHaveLength(1);
    expect(frames[0]!.payload.length).toBe(MAX_FRAME_BYTES);
  });
});
