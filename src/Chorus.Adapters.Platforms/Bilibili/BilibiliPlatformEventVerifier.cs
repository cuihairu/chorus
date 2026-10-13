using Chorus.Adapters.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Verifies posts to /ingress/bilibili using the B站 open-live signature
/// scheme (x-bili-* headers + HMAC-SHA256, blivedm <c>_request_open_live</c>,
/// ✅ 原文核实). Posters today are chorus's own <see cref="BilibiliOpenLiveClient"/>
/// bridging wss events into the shared ingress pipeline; the scheme matches
/// the upstream algorithm so an external B站-style signer interoperates too.
/// Fails closed whenever credentials are missing or anything is unverifiable.
/// </summary>
public sealed class BilibiliPlatformEventVerifier : IPlatformNamedEventVerifier
{
    private readonly string? _accessKeyId;
    private readonly string? _accessKeySecret;
    private readonly int _timestampToleranceSeconds;
    private readonly ILogger<BilibiliPlatformEventVerifier>? _logger;
    private int _missingCredentialsLogged;

    public string PlatformName => PlatformNames.Bilibili;

    public BilibiliPlatformEventVerifier(
        string? accessKeyId,
        string? accessKeySecret,
        int timestampToleranceSeconds = 300,
        ILogger<BilibiliPlatformEventVerifier>? logger = null)
    {
        _accessKeyId = Normalize(accessKeyId);
        _accessKeySecret = Normalize(accessKeySecret);
        _timestampToleranceSeconds = timestampToleranceSeconds;
        _logger = logger;
    }

    public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (!string.Equals(platform, PlatformNames.Bilibili, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_accessKeyId == null || _accessKeySecret == null)
        {
            if (Interlocked.Exchange(ref _missingCredentialsLogged, 1) == 0)
            {
                _logger?.LogError(
                    "Bilibili ingress verifier has no credentials configured; all callbacks are rejected (fail closed). Set Ingress:Bilibili:AccessKeyId and Ingress:Bilibili:AccessKeySecret.");
            }

            return false;
        }

        if (rawBody == null || headers == null)
        {
            return false;
        }

        return BilibiliSigner.VerifyHeaders(
            rawBody,
            _accessKeyId,
            _accessKeySecret,
            headers,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            _timestampToleranceSeconds);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
