using System.Buffers.Binary;

namespace Chorus.Messaging.Transports;

/// <summary>Frame writer for the length-prefixed stream protocol shared by the
/// stream transports (UDS / TCP / NamedPipe): a little-endian 4-byte payload
/// length followed by the payload. Reading is <see cref="FrameReader"/> -
/// frames pipelined out of one read instead of one read per frame.</summary>
public static class LengthPrefixedStream
{
    // Below this size the merged write pays for itself: one send() instead of
    // two queued writes beats copying the payload into a second buffer.
    private const int InlineFrameThreshold = 16 * 1024;

    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length <= InlineFrameThreshold)
        {
            // Header and payload ride the same buffer: the receiver is a byte
            // stream and never sees write boundaries, so merging cannot break
            // framing.
            var frame = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
            payload.Span.CopyTo(frame.AsSpan(4));
            await stream.WriteAsync(frame, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return;
        }

        // Larger frames: the merge would duplicate the whole payload just to
        // save a syscall.
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header.ToArray(), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
