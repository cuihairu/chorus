using System.Buffers.Binary;
using Chorus.Messaging.Transports;

namespace Chorus.Engine.Tests;

/// <summary>
/// Frame-layer unit tests (LengthPrefixedStream / FrameReader). The three
/// stream transports (UDS / TCP / NamedPipe) share this codec: the reader
/// must pipeline frames (one read may satisfy many frames) without changing
/// the wire bytes, and every malformed-input behaviour must stay identical
/// to the old per-frame reader - the transports rely on these failure modes
/// to tear down dead connections.
/// </summary>
public sealed class LengthPrefixedStreamTests
{
    private static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static async Task WriteFrame(Stream stream, byte[] payload)
        => await LengthPrefixedStream.WriteFrameAsync(stream, payload, CancellationToken.None);

    /// <summary>Serves scripted bytes across ReadAsync with a per-call cap
    /// (cap=1 exercises split frames) and counts read syscalls - the whole
    /// point of the pipelined reader is fewer reads per frame.</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _cap;
        private int _pos;
        public int ReadCalls { get; private set; }

        public ScriptedStream(byte[] data, int cap = int.MaxValue)
        {
            _data = data;
            _cap = cap;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Yield();
            ReadCalls++;
            var take = Math.Min(Math.Min(buffer.Length, _data.Length - _pos), _cap);
            _data.AsSpan(_pos, take).CopyTo(buffer.Span);
            _pos += take;
            return take;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    [Fact]
    public async Task FrameReader_YieldsManyFramesPerRead()
    {
        var wire = new MemoryStream();
        await WriteFrame(wire, "one"u8.ToArray());
        await WriteFrame(wire, "two"u8.ToArray());
        await WriteFrame(wire, "three"u8.ToArray());

        var stream = new ScriptedStream(wire.ToArray());
        var reader = new FrameReader(stream);

        var first = await reader.ReadFrameAsync(CancellationToken.None);
        var second = await reader.ReadFrameAsync(CancellationToken.None);
        var third = await reader.ReadFrameAsync(CancellationToken.None);
        var eof = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.Null(eof);
        Assert.Equal("one"u8.ToArray(), first);
        Assert.Equal("two"u8.ToArray(), second);
        Assert.Equal("three"u8.ToArray(), third);
        // 3 frames + the EOF probe = 2 reads total; the old per-frame reader
        // needed 6 (two per frame) plus one for EOF.
        Assert.Equal(2, stream.ReadCalls);
    }

    [Fact]
    public async Task FrameReader_SurvivesSingleByteDribble()
    {
        var wire = new MemoryStream();
        await WriteFrame(wire, "alpha"u8.ToArray());
        await WriteFrame(wire, "beta"u8.ToArray());

        var reader = new FrameReader(new ScriptedStream(wire.ToArray(), cap: 1));

        Assert.Equal("alpha"u8.ToArray(), await reader.ReadFrameAsync(CancellationToken.None));
        Assert.Equal("beta"u8.ToArray(), await reader.ReadFrameAsync(CancellationToken.None));
        Assert.Null(await reader.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FrameReader_SmallInternalBufferStillDecodes()
    {
        var wire = new MemoryStream();
        await WriteFrame(wire, "across-boundary"u8.ToArray());
        await WriteFrame(wire, "next"u8.ToArray());

        // 16-byte internal buffer: the first frame's payload spans multiple
        // buffer fills, exercising the reclaim path between frames.
        var reader = new FrameReader(new ScriptedStream(wire.ToArray(), cap: 7), bufferSize: 16);

        Assert.Equal("across-boundary"u8.ToArray(), await reader.ReadFrameAsync(CancellationToken.None));
        Assert.Equal("next"u8.ToArray(), await reader.ReadFrameAsync(CancellationToken.None));
        Assert.Null(await reader.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FrameReader_LargeFrameReadsPastBuffer()
    {
        // A frame bigger than the internal buffer must not force the buffer
        // to grow: the payload beyond the buffered prefix is read straight
        // into the frame array.
        var payload = new byte[200 * 1024];
        new Random(42).NextBytes(payload);
        var wire = new MemoryStream();
        await WriteFrame(wire, payload);

        var reader = new FrameReader(new ScriptedStream(wire.ToArray(), cap: 8 * 1024), bufferSize: 64 * 1024);

        Assert.Equal(payload, await reader.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FrameReader_TruncatedHeader_Throws()
    {
        var reader = new FrameReader(new ScriptedStream(new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAsync<IOException>(() => reader.ReadFrameAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task FrameReader_TruncatedPayload_Throws()
    {
        var frame = Frame("payload-longer-than-wire"u8.ToArray());
        var truncated = frame.AsSpan(0, frame.Length - 5).ToArray();

        var reader = new FrameReader(new ScriptedStream(truncated));
        await Assert.ThrowsAsync<IOException>(() => reader.ReadFrameAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32 * 1024 * 1024 + 1)]
    public async Task FrameReader_RejectsInvalidLength(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        var reader = new FrameReader(new ScriptedStream(header));

        await Assert.ThrowsAsync<IOException>(() => reader.ReadFrameAsync(CancellationToken.None).AsTask());
    }

    /// <summary>Records WriteAsync calls so tests can pin how many syscalls
    /// one frame costs - the write-side merge exists to halve that.</summary>
    private sealed class RecordingStream : MemoryStream
    {
        public int WriteCalls { get; private set; }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task WriteFrameAsync_SmallPayloadIsOneWrite()
    {
        var payload = new byte[1024];
        new Random(1).NextBytes(payload);

        var wire = new RecordingStream();
        await WriteFrame(wire, payload);

        // One write = one send() on a real transport: header and payload ride
        // the same buffer instead of two queued writes.
        Assert.Equal(1, wire.WriteCalls);
        Assert.Equal(4 + payload.Length, wire.Length);
        Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32LittleEndian(wire.GetBuffer().AsSpan(0, 4)));
        Assert.Equal(payload, wire.GetBuffer().AsSpan(4).ToArray());
    }

    [Fact]
    public async Task WriteFrameAsync_LargePayloadWritesHeaderSeparately()
    {
        // The single-write merge is capped: a large frame must not be copied
        // into a second buffer of its own size just to save one syscall.
        var payload = new byte[64 * 1024];
        new Random(7).NextBytes(payload);

        var wire = new RecordingStream();
        await WriteFrame(wire, payload);

        Assert.Equal(2, wire.WriteCalls);
        var reader = new FrameReader(new ScriptedStream(wire.ToArray()));
        Assert.Equal(payload, await reader.ReadFrameAsync(CancellationToken.None));
    }
}
