using System.Buffers.Binary;

namespace Chorus.Messaging.Transports;

internal static class LengthPrefixedStream
{
    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header.ToArray(), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var readHeader = await ReadExactAsync(stream, header, cancellationToken);
        if (readHeader == 0)
        {
            return null;
        }
        if (readHeader != 4)
        {
            throw new IOException("Failed to read frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > 32 * 1024 * 1024)
        {
            throw new IOException($"Invalid frame length: {length}");
        }

        var payload = new byte[length];
        var readPayload = await ReadExactAsync(stream, payload, cancellationToken);
        if (readPayload != length)
        {
            throw new IOException("Unexpected end of stream.");
        }
        return payload;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (n == 0)
            {
                return total;
            }
            total += n;
        }
        return total;
    }
}
