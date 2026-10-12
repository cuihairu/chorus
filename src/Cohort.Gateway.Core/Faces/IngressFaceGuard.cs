namespace Cohort.Gateway.Core.Faces;

/// <summary>
/// Guard applied to every platform-ingress request: rate limit, bounded body
/// read, and a processing timeout. Owned by the platform-ingress face; the
/// streamer-ws face has its own policy types.
/// </summary>
public sealed class IngressFaceGuard
{
    private readonly IngressFaceOptions _options;
    private readonly IngressRateLimiter _rateLimiter;

    public IngressFaceGuard(IngressFaceOptions options)
    {
        _options = options;
        _rateLimiter = new IngressRateLimiter(options.RateLimitPerMinute);
    }

    /// <summary>Rate-limit gate. Call before reading the body; rejected requests get 429.</summary>
    public bool AllowRequest(string sourceKey, long nowMs) => _rateLimiter.Allow(sourceKey, nowMs);

    /// <summary>Reads the body while enforcing <see cref="IngressFaceOptions.MaxBodyBytes"/>.</summary>
    public async Task<IngressBodyReadResult> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        const int chunkSize = 8 * 1024;
        using var buffer = new MemoryStream(capacity: chunkSize);
        var scratch = new byte[chunkSize];
        long total = 0;

        while (true)
        {
            var read = await body.ReadAsync(scratch.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > _options.MaxBodyBytes)
            {
                return new IngressBodyReadResult(Ok: false, TooLarge: true, Body: null);
            }

            buffer.Write(scratch, 0, read);
        }

        return new IngressBodyReadResult(Ok: true, TooLarge: false, Body: System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>
    /// Combined budget for the whole request (body read + verify + map + ingest):
    /// request abort plus the face timeout, whichever fires first.
    /// </summary>
    public CancellationTokenSource CreateRequestCancellationToken(CancellationToken requestAborted)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, _options.TimeoutMs)));
        return cts;
    }
}

public readonly record struct IngressBodyReadResult(bool Ok, bool TooLarge, string? Body);
