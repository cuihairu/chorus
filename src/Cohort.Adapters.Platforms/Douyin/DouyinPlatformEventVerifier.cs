using System.Security.Cryptography;
using System.Text;
using Cohort.Adapters.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cohort.Adapters.Platforms;

/// <summary>
/// Verifies Douyin live event callbacks.
///
/// STATUS (2026-10-12): the official callback signature algorithm could not be
/// confirmed online (docs are login-gated; see docs/reference/platform-douyin.md).
/// The implementation below is the placeholder contract agreed for integration
/// testing: HMAC-SHA256 over the raw request body, lowercase hex, header name
/// configurable. It fails closed in every unresolvable case.
///
/// TODO(joint-debugging): replace <see cref="ComputeSignature"/> and the header
/// name with the official algorithm once callback access is granted.
/// </summary>
public sealed class DouyinPlatformEventVerifier : IPlatformNamedEventVerifier
{
    public const string DefaultSignatureHeader = "X-Douyin-Signature";

    private readonly string? _secret;
    private readonly string _signatureHeader;
    private readonly ILogger<DouyinPlatformEventVerifier>? _logger;
    private int _missingSecretLogged;

    public string PlatformName => PlatformNames.Douyin;

    public DouyinPlatformEventVerifier(
        string? secret,
        string? signatureHeader = null,
        ILogger<DouyinPlatformEventVerifier>? logger = null)
    {
        _secret = NormalizeSecret(secret);
        _signatureHeader = string.IsNullOrWhiteSpace(signatureHeader)
            ? DefaultSignatureHeader
            : signatureHeader.Trim();
        _logger = logger;
    }

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
        return FixedTimeEqualsHex(expected, provided.Trim());
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
