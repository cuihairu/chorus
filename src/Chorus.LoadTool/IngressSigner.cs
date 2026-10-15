using System.Security.Cryptography;
using System.Text;

namespace Chorus.LoadTool;

/// <summary>
/// Client-side Douyin ingress signature: the documented placeholder contract
/// the gateway verifies (HMAC-SHA256(secret, rawBody) as lowercase hex in the
/// X-Douyin-Signature header). Mirrors DouyinPlatformEventVerifier so the load
/// tool can drive a gateway that has Ingress:Douyin:AppSecret configured
/// instead of burning the whole run on fail-closed 401s.
/// </summary>
public static class IngressSigner
{
    /// <summary>Default signature header the gateway verifier reads.</summary>
    public const string SignatureHeader = "X-Douyin-Signature";

    /// <summary>
    /// HMAC-SHA256(secret, rawBody) as lowercase hex over the exact body bytes
    /// that are posted (UTF-8), which is what the gateway hashes back.
    /// </summary>
    public static string ComputeSignature(string rawBody, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var payload = Encoding.UTF8.GetBytes(rawBody);
        var hash = HMACSHA256.HashData(key, payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
