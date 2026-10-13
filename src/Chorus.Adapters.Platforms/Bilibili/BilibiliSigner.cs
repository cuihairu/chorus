using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// B站 open-live HTTP 签名（x-bili-* 头 + HMAC-SHA256），与 blivedm
/// <c>clients/open_live.py</c> 的 <c>_request_open_live</c> 逐字段一致（✅ 原文核实）：
/// 六个 x-bili-* 头拼成 "k:v" 行、'\n' 连接，HMAC-SHA256(access_key_secret)
/// 小写 hex 放入 Authorization；Authorization 自身不参与签名串；
/// content-md5 为请求体 MD5 小写 hex；timestamp 为秒级 epoch 字符串。
/// 同一套规范化供 <see cref="BilibiliOpenLiveClient"/>（签名侧）与
/// <see cref="BilibiliPlatformEventVerifier"/>（验签侧）共用。
/// </summary>
public static class BilibiliSigner
{
    public const string AccessKeyIdHeader = "x-bili-accesskeyid";
    public const string ContentMd5Header = "x-bili-content-md5";
    public const string SignatureMethodHeader = "x-bili-signature-method";
    public const string SignatureNonceHeader = "x-bili-signature-nonce";
    public const string SignatureVersionHeader = "x-bili-signature-version";
    public const string TimestampHeader = "x-bili-timestamp";
    public const string AuthorizationHeader = "Authorization";

    public const string SignatureMethodValue = "HMAC-SHA256";
    public const string SignatureVersionValue = "1.0";

    // blivedm 的插入序恰好是字母序；显式排序保证签名/验签两侧同一规范化。
    private static readonly string[] CanonicalHeaderNames =
    [
        AccessKeyIdHeader,
        ContentMd5Header,
        SignatureMethodHeader,
        SignatureNonceHeader,
        SignatureVersionHeader,
        TimestampHeader,
    ];

    /// <summary>为一具请求体构造完整签名头（含 Authorization）。</summary>
    public static IReadOnlyDictionary<string, string> BuildHeaders(
        string body, string accessKeyId, string accessKeySecret, long timestampSeconds, string nonce)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccessKeyIdHeader] = accessKeyId,
            [ContentMd5Header] = Md5Hex(body),
            [SignatureMethodHeader] = SignatureMethodValue,
            [SignatureNonceHeader] = nonce,
            [SignatureVersionHeader] = SignatureVersionValue,
            [TimestampHeader] = timestampSeconds.ToString(CultureInfo.InvariantCulture),
        };
        headers[AuthorizationHeader] = ComputeSignature(headers, accessKeySecret);
        return headers;
    }

    /// <summary>
    /// 校验一组签名头：头齐全、key id 匹配、timestamp 未过期、content-md5 对体、
    /// Authorization 对签名串。任何一步不可解都拒绝（fail-closed）。
    /// </summary>
    public static bool VerifyHeaders(
        string rawBody,
        string accessKeyId,
        string accessKeySecret,
        IReadOnlyDictionary<string, string> headers,
        long nowSeconds,
        int timestampToleranceSeconds = 300)
    {
        if (string.IsNullOrEmpty(rawBody) || headers == null)
        {
            return false;
        }

        if (!headers.TryGetValue(AccessKeyIdHeader, out var keyId)
            || !string.Equals(keyId, accessKeyId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!headers.TryGetValue(TimestampHeader, out var tsText)
            || !long.TryParse(tsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)
            || Math.Abs(nowSeconds - ts) > timestampToleranceSeconds)
        {
            return false;
        }

        if (!headers.TryGetValue(ContentMd5Header, out var md5)
            || !string.Equals(md5, Md5Hex(rawBody), StringComparison.Ordinal))
        {
            return false;
        }

        if (!headers.TryGetValue(AuthorizationHeader, out var provided))
        {
            return false;
        }

        var expected = ComputeSignature(headers, accessKeySecret);
        return expected.Length != 0 && FixedTimeEqualsHex(expected, provided);
    }

    internal static string ComputeSignature(IReadOnlyDictionary<string, string> headers, string accessKeySecret)
    {
        var sb = new StringBuilder();
        foreach (var name in CanonicalHeaderNames)
        {
            if (!headers.TryGetValue(name, out var value))
            {
                return string.Empty;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(name).Append(':').Append(value);
        }

        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(accessKeySecret),
            Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Md5Hex(string body)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static bool FixedTimeEqualsHex(string expectedHex, string providedHex)
    {
        if (string.IsNullOrWhiteSpace(providedHex))
        {
            return false;
        }

        try
        {
            var provided = Convert.FromHexString(providedHex);
            var expected = Convert.FromHexString(expectedHex);
            return CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
