using System.Buffers;
using System.Buffers.Binary;

namespace Chorus.Messaging.Transports;

/// <summary>
/// Pipelined reader for the length-prefixed frame stream shared by the stream
/// transports (UDS / TCP / NamedPipe). One ReadAsync can return many frames,
/// so a burst of small frames costs a couple of reads per burst instead of two
/// reads per frame - the measured ceiling on the split-mode eng-to-gw channel
/// was per-message I/O, not throughput.
///
/// Frame payloads come straight out of the buffer, or are read straight from
/// the stream when the frame outgrows it, so the buffer never grows with frame
/// size. The wire bytes and every failure mode are unchanged from the old
/// per-frame reader: clean EOF returns null, a truncated header or payload
/// throws IOException - the transports rely on those to tear down dead
/// connections.
/// </summary>
public sealed class FrameReader : IDisposable
{
    private const int MaxFrameBytes = 32 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _length;
    private int _pos;

    public FrameReader(Stream stream, int bufferSize = 64 * 1024)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    /// <summary>
    /// Dispose after the read loop ends: an outstanding async read may still
    /// hold the rented buffer.
    /// </summary>
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }

    public async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureAvailableAsync(4, cancellationToken))
        {
            // Nothing at all on the wire is a clean EOF; a partial header is a
            // torn stream, not an end of stream.
            if (_length - _pos > 0)
            {
                throw new IOException("Failed to read frame header.");
            }
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_pos, 4));
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new IOException($"Invalid frame length: {length}");
        }
        _pos += 4;

        var payload = new byte[length];
        var buffered = Math.Min(length, _length - _pos);
        _buffer.AsSpan(_pos, buffered).CopyTo(payload);
        _pos += buffered;

        var remaining = length - buffered;
        while (remaining > 0)
        {
            // The frame is bigger than the buffer: read the rest straight into
            // the frame array instead of growing the buffer with frame size.
            var read = await _stream.ReadAsync(payload.AsMemory(length - remaining, remaining), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Unexpected end of stream.");
            }
            remaining -= read;
        }

        if (_pos > _length / 2)
        {
            Compact();
        }
        return payload;
    }

    private async Task<bool> EnsureAvailableAsync(int count, CancellationToken cancellationToken)
    {
        while (_length - _pos < count)
        {
            // Reclaim the consumed prefix; a frame this big is only ever the
            // header, so the buffer always has room after compaction.
            if (_length == _buffer.Length)
            {
                Compact();
            }

            var read = await _stream.ReadAsync(_buffer.AsMemory(_length, _buffer.Length - _length), cancellationToken);
            if (read == 0)
            {
                return _length - _pos >= count;
            }
            _length += read;
        }
        return true;
    }

    private void Compact()
    {
        var kept = _length - _pos;
        if (kept > 0)
        {
            Buffer.BlockCopy(_buffer, _pos, _buffer, 0, kept);
        }
        _length = kept;
        _pos = 0;
    }
}
