using System.Security.Cryptography;
using System.Text;

namespace Chorus.LoadTool;

/// <summary>
/// Client-side ingress signatures for the load tool, mirroring the gateway's
/// own platform verifiers so a signed load run measures the engine instead of
/// silently measuring its own fail-closed 401s (2026-10-15 run: ok=0,
/// other=6316). Supported: douyin (placeholder HMAC-SHA256 scheme) and
/// kuaishou (official sha1Hex(body + token)). Other platforms get no
/// signature: the tool refuses to invent a scheme it cannot verify against
/// the gateway's code. Construction parity with the verifiers is pinned by
/// IngressSignerTests (verifier round-trip + independent known vectors).
/// </summary>
public static class IngressSigner
{
    /// <summary>A signature header the driver attaches to an ingress post.</summary>
    public readonly record struct Signature(string Name, string Value);

    public const string DouyinSignatureHeader = "X-Douyin-Signature";
    public const string KuaishouSignatureHeader = "kwaisign";

    public static bool CanSign(string platform)
        => IsDouyin(platform) || IsKuaishou(platform);

    /// <summary>Signature header for an ingress post, or null when the secret
    /// is blank (same normalization the gateway verifiers apply: no secret
    /// means nothing to sign with) or the platform has no supported scheme.</summary>
    public static Signature? Sign(string platform, string rawBody, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var key = secret.Trim();
        if (IsDouyin(platform))
        {
            return new Signature(DouyinSignatureHeader, DouyinSignature(rawBody, key));
        }

        return IsKuaishou(platform)
            ? new Signature(KuaishouSignatureHeader, KuaishouSignature(rawBody, key))
            : null;
    }

    /// <summary>Douyin placeholder: HMAC-SHA256(secret, rawBody) as lowercase hex.</summary>
    public static string DouyinSignature(string rawBody, string secret)
        => Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

    /// <summary>Kuaishou official: sha1Hex(rawBody + token) as lowercase hex.</summary>
    public static string KuaishouSignature(string rawBody, string token)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(rawBody + token))).ToLowerInvariant();

    private static bool IsDouyin(string? platform)
        => string.Equals(platform?.Trim(), "douyin", StringComparison.OrdinalIgnoreCase);

    private static bool IsKuaishou(string? platform)
        => string.Equals(platform?.Trim(), "kuaishou", StringComparison.OrdinalIgnoreCase);
}
