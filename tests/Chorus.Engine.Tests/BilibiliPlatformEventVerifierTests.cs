using Chorus.Adapters.Platforms;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// 验签器包装层的 fail-closed 分支：错平台、缺凭据、空白凭据归一化、
/// 空 body/头一律拒绝；签名正确才放行（其余签名细节由 BilibiliSignerTests 金测）。
/// </summary>
public sealed class BilibiliPlatformEventVerifierTests
{
    private const string KeyId = "key-1";
    private const string Secret = "secret-1";
    private const string Body = "hello";

    private static IReadOnlyDictionary<string, string> Signed()
        => BilibiliSigner.BuildHeaders(Body, KeyId, Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "nonce");

    [Fact]
    public void WrongPlatform_ReturnsFalse()
    {
        var verifier = new BilibiliPlatformEventVerifier(KeyId, Secret);

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Signed()));
    }

    [Fact]
    public void PlatformMatch_IsCaseInsensitive()
    {
        var verifier = new BilibiliPlatformEventVerifier(KeyId, Secret);

        Assert.True(verifier.Verify("BILIBILI", Body, Signed()));
    }

    [Fact]
    public void MissingCredentials_FailClosed_AndLogsOnce()
    {
        var verifier = new BilibiliPlatformEventVerifier(null, null, logger: NullLogger<BilibiliPlatformEventVerifier>.Instance);

        // 两次调用都必须拒绝（记一次错的幂等不打重复日志）。
        Assert.False(verifier.Verify(PlatformNames.Bilibili, Body, Signed()));
        Assert.False(verifier.Verify(PlatformNames.Bilibili, Body, Signed()));
    }

    [Fact]
    public void WhitespaceCredentials_NormalizedToMissing_FailClosed()
    {
        var verifier = new BilibiliPlatformEventVerifier("  ", "\t");

        Assert.False(verifier.Verify(PlatformNames.Bilibili, Body, Signed()));
    }

    [Fact]
    public void NullBodyOrHeaders_ReturnsFalse()
    {
        var verifier = new BilibiliPlatformEventVerifier(KeyId, Secret);

        Assert.False(verifier.Verify(PlatformNames.Bilibili, null!, Signed()));
        Assert.False(verifier.Verify(PlatformNames.Bilibili, Body, null!));
    }

    [Fact]
    public void CorrectSignature_Passes()
    {
        var verifier = new BilibiliPlatformEventVerifier(KeyId, Secret);

        Assert.True(verifier.Verify(PlatformNames.Bilibili, Body, Signed()));
    }
}
