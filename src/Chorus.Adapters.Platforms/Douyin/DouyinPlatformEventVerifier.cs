using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Verifies Douyin live event callbacks.
///
/// STATUS (2026-10-12): the official callback signature algorithm could not be
/// confirmed online (docs are login-gated; see docs/reference/platform-douyin.md).
/// The implementation below is the placeholder contract agreed for integration
/// testing: HMAC-SHA256 over the raw request body, lowercase hex, header name
/// configurable. It fails closed in every unresolvable case.
///
/// FRESHNESS (review 2026-11-13 M5): the body must carry a millisecond
/// <c>timestamp</c> field — inside the signature's coverage, so the bound is
/// signature-protected. A payload older or newer than the tolerance is refused:
/// a valid signature alone must not let a captured callback replay forever.
/// TODO(joint-debugging): replace <see cref="ComputeSignature"/> and the header
/// name with the official algorithm once callback access is granted; keep the
/// freshness bound (adjust the field name if the official protocol differs).
/// </summary>
public sealed class DouyinPlatformEventVerifier : IPlatformNamedEventVerifier, IPlatformTrustedAppIdSource
{
    public const string DefaultSignatureHeader = "X-Douyin-Signature";
    public const int DefaultTimestampToleranceSeconds = 300;

    private readonly string? _secret;
    private readonly string? _appId;
    private readonly string _signatureHeader;
    private readonly int _timestampToleranceSeconds;
    private readonly ILogger<DouyinPlatformEventVerifier>? _logger;
    private int _missingSecretLogged;

    public string PlatformName => PlatformNames.Douyin;

    public DouyinPlatformEventVerifier(
        string? secret,
        string? appId = null,
        string? signatureHeader = null,
        ILogger<DouyinPlatformEventVerifier>? logger = null,
        int? timestampToleranceSeconds = null)
    {
        _secret = NormalizeSecret(secret);
        _appId = NormalizeAppId(appId);
        _signatureHeader = string.IsNullOrWhiteSpace(signatureHeader)
            ? DefaultSignatureHeader
            : signatureHeader.Trim();
        _timestampToleranceSeconds = timestampToleranceSeconds is > 0
            ? timestampToleranceSeconds.Value
            : DefaultTimestampToleranceSeconds;
        _logger = logger;
    }

    // The secret alone proves nothing nameable, so no trusted identity is claimed
    // by default; when Ingress:Douyin:AppId is configured it binds the ingress
    // X-Chorus-App-Id header to the credential's app (review C1).
    public string? GetTrustedAppId(string platform)
        => string.Equals(platform, PlatformNames.Douyin, StringComparison.OrdinalIgnoreCase)
            ? _appId
            : null;

    public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (!string.Equals(platform, PlatformNames.Douyin, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_secret == null)
        {
            if (Interlocked.Exchange(ref _missingSecretLogged, 1) == 0)
            {
                _logger?.LogError(
                    "Douyin ingress verifier has no secret configured; all callbacks are rejected (fail closed). Set Ingress:Douyin:AppSecret.");
            }

            return false;
        }

        if (rawBody == null)
        {
            return false;
        }

        if (!headers.TryGetValue(_signatureHeader, out var provided) || string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var expected = ComputeSignature(rawBody, _secret);
        if (!FixedTimeEqualsHex(expected, provided.Trim()))
        {
            return false;
        }

        // Freshness rides after the signature: the timestamp lives inside the signed
        // body, so only a signature-valid payload can reach this check, and a replay
        // of any age is refused here (missing / non-numeric / out of window).
        return IsFreshTimestamp(rawBody, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _timestampToleranceSeconds);
    }

    /// <summary>
    /// Signature-protected freshness: the body's millisecond <c>timestamp</c> must sit
    /// within the tolerance of <paramref name="nowMs"/> in both directions. Absent or
    /// malformed timestamps fail closed - "no freshness field" means "infinitely stale".
    /// </summary>
    public static bool IsFreshTimestamp(string rawBody, long nowMs, int toleranceSeconds)
    {
        long? timestamp;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            timestamp = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("timestamp", out var ts)
                && ts.ValueKind == JsonValueKind.Number
                && ts.TryGetInt64(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return false;
        }

        return timestamp is { } valueTs
            && Math.Abs(nowMs - valueTs) <= (long)toleranceSeconds * 1000;
    }

    /// <summary>
    /// Placeholder signature: HMAC-SHA256(secret, rawBody) as lowercase hex.
    /// TODO(joint-debugging): swap for the official construction.
    /// </summary>
    public static string ComputeSignature(string rawBody, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var payload = Encoding.UTF8.GetBytes(rawBody);
        var hash = HMACSHA256.HashData(key, payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var trimmed = secret.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NormalizeAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        var trimmed = appId.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool FixedTimeEqualsHex(string expectedHex, string providedHex)
    {
        if (!TryDecodeHex(providedHex, out var provided))
        {
            return false;
        }

        var expected = Convert.FromHexString(expectedHex);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static bool TryDecodeHex(string value, out byte[] bytes)
    {
        // Convert.FromHexString throws on bad input; guard for tampered payloads.
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }
}
