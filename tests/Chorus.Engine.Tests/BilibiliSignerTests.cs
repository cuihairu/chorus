using System.Security.Cryptography;
using System.Text;
using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

/// <summary>
/// B站 open-live 签名金测（blivedm _request_open_live 算法，✅ 原文核实）：
/// 六个 x-bili-* 头 "k:v" 行 '\n' 连接、HMAC-SHA256 小写 hex 进 Authorization、
/// content-md5 为体 MD5 小写 hex；验签侧 fail-closed。
/// </summary>
public sealed class BilibiliSignerTests
{
    private const string KeyId = "key-1";
    private const string Secret = "secret-1";
    private const long Ts = 1700000000;
    private const string Nonce = "nonce-1";
    private const string Body = "hello";
    // md5("hello") 小写 hex，钉死 canonical 里的 content-md5 位。
    private const string BodyMd5 = "5d41402abc4b2a76b9719d911017c592";

    /// <summary>blivedm 的规范化串（六头按字母序、k:v 行、'\n' 连接）。</summary>
    private static string ExpectedCanonical => string.Join('\n',
        $"x-bili-accesskeyid:{KeyId}",
        $"x-bili-content-md5:{BodyMd5}",
        "x-bili-signature-method:HMAC-SHA256",
        $"x-bili-signature-nonce:{Nonce}",
        "x-bili-signature-version:1.0",
        $"x-bili-timestamp:{Ts}");

    private static string ExpectedSignature()
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes(ExpectedCanonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> Build() =>
        BilibiliSigner.BuildHeaders(Body, KeyId, Secret, Ts, Nonce);

    private static Dictionary<string, string> AsVerifyDict(IReadOnlyDictionary<string, string> headers) =>
        new(headers, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void BuildHeaders_PinsTheCanonicalLayout_AndBlivedmSignature()
    {
        var headers = Build();

        Assert.Equal(KeyId, headers[BilibiliSigner.AccessKeyIdHeader]);
        Assert.Equal(BodyMd5, headers[BilibiliSigner.ContentMd5Header]);
        Assert.Equal("HMAC-SHA256", headers[BilibiliSigner.SignatureMethodHeader]);
        Assert.Equal(Nonce, headers[BilibiliSigner.SignatureNonceHeader]);
        Assert.Equal("1.0", headers[BilibiliSigner.SignatureVersionHeader]);
        Assert.Equal("1700000000", headers[BilibiliSigner.TimestampHeader]);
        Assert.Equal(ExpectedSignature(), headers[BilibiliSigner.AuthorizationHeader]);
    }

    [Fact]
    public void VerifyHeaders_CorrectSignature_Accepts()
    {
        Assert.True(BilibiliSigner.VerifyHeaders(Body, KeyId, Secret, AsVerifyDict(Build()), Ts + 1));
    }

    [Fact]
    public void VerifyHeaders_TamperedBody_Rejected()
    {
        Assert.False(BilibiliSigner.VerifyHeaders("hello-tampered", KeyId, Secret, AsVerifyDict(Build()), Ts));
    }

    [Fact]
    public void VerifyHeaders_WrongAuthorization_Rejected()
    {
        var headers = new Dictionary<string, string>(Build(), StringComparer.OrdinalIgnoreCase)
        {
            [BilibiliSigner.AuthorizationHeader] = new string('0', 64),
        };
        Assert.False(BilibiliSigner.VerifyHeaders(Body, KeyId, Secret, headers, Ts));
    }

    [Fact]
    public void VerifyHeaders_MissingHeader_Rejected()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Build())
        {
            if (name != BilibiliSigner.SignatureNonceHeader)
            {
                headers[name] = value;
            }
        }

        Assert.False(BilibiliSigner.VerifyHeaders(Body, KeyId, Secret, headers, Ts));
    }

    [Fact]
    public void VerifyHeaders_StaleTimestamp_Rejected()
    {
        // 默认容忍 300s；601s 前的签名过期。
        Assert.False(BilibiliSigner.VerifyHeaders(Body, KeyId, Secret, AsVerifyDict(Build()), Ts + 601));
    }

    [Fact]
    public void VerifyHeaders_WrongAccessKeyId_Rejected()
    {
        Assert.False(BilibiliSigner.VerifyHeaders(Body, "other-key", Secret, AsVerifyDict(Build()), Ts));
    }

    [Fact]
    public void VerifyHeaders_WrongSecret_Rejected()
    {
        Assert.False(BilibiliSigner.VerifyHeaders(Body, KeyId, "other-secret", AsVerifyDict(Build()), Ts));
    }

    [Fact]
    public void VerifyHeaders_GarbageAuthorization_Rejected()
    {
        var headers = new Dictionary<string, string>(Build(), StringComparer.OrdinalIgnoreCase)
        {
            [BilibiliSigner.AuthorizationHeader] = "not-hex-at-all",
        };
        Assert.False(BilibiliSigner.VerifyHeaders(Body, KeyId, Secret, headers, Ts));
    }
}
