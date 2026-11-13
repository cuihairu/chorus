using System.Security.Cryptography;
using System.Text;
using Chorus.Adapters.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Verifies Kuaishou event callbacks.
///
/// SIGNATURE: official Kuaishou algorithm per
/// https://open.kuaishou.com/docs/saas/develop/api/encryptedMsg.html
/// (消息加解密和签名验证, updated 2023-02-08):
///   sha1Hex(rawBody + token) must equal the "kwaisign" request header,
///   where token is the developer console's 消息验证Token.
///
/// STATUS (2026-10-12): signature and AES envelope are official; whether the
/// live (danmaku/gift) event channel uses this exact envelope is confirmed only
/// at joint debugging - see docs/reference/platform-kuaishou.md.
/// Fails closed when the token is not configured.
/// </summary>
public sealed class KuaishouPlatformEventVerifier : IPlatformNamedEventVerifier, IPlatformTrustedAppIdSource
{
    public const string DefaultSignatureHeader = "kwaisign";

    private readonly string? _token;
    private readonly string? _appId;
    private readonly string _signatureHeader;
    private readonly ILogger<KuaishouPlatformEventVerifier>? _logger;
    private int _missingTokenLogged;

    public string PlatformName => PlatformNames.Kuaishou;

    public KuaishouPlatformEventVerifier(
        string? verifyToken,
        string? appId = null,
        string? signatureHeader = null,
        ILogger<KuaishouPlatformEventVerifier>? logger = null)
    {
        _token = NormalizeToken(verifyToken);
        _appId = NormalizeAppId(appId);
        _signatureHeader = string.IsNullOrWhiteSpace(signatureHeader)
            ? DefaultSignatureHeader
            : signatureHeader.Trim();
        _logger = logger;
    }

    // The verify token alone proves nothing nameable, so no trusted identity is
    // claimed by default; when Ingress:Kuaishou:AppId is configured it binds the
    // ingress X-Chorus-App-Id header to the credential's app (review C1).
    public string? GetTrustedAppId(string platform)
        => string.Equals(platform, PlatformNames.Kuaishou, StringComparison.OrdinalIgnoreCase)
            ? _appId
            : null;

    public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (!string.Equals(platform, PlatformNames.Kuaishou, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_token == null)
        {
            if (Interlocked.Exchange(ref _missingTokenLogged, 1) == 0)
            {
                _logger?.LogError(
                    "Kuaishou ingress verifier has no verify token configured; all callbacks are rejected (fail closed). Set Ingress:Kuaishou:VerifyToken.");
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

        var expected = ComputeSignature(rawBody, _token);
        return string.Equals(expected, provided.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Official Kuaishou signature: sha1Hex(bodyStr + tokenStr).</summary>
    public static string ComputeSignature(string rawBody, string token)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(rawBody + token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
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
}
