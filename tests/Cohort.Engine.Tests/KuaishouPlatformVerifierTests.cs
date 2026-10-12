using System.Security.Cryptography;
using System.Text;
using Cohort.Adapters.Platforms;

namespace Cohort.Engine.Tests;

public sealed class KuaishouPlatformVerifierTests
{
    private const string Token = "ks-verify-token";
    private const string Body = """{"encryptedMsg":"abc","msgId":"m-1","componentAppId":"ks-app","timestamp":1700000000000}""";

    private static IReadOnlyDictionary<string, string> Headers(string signature)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [KuaishouPlatformEventVerifier.DefaultSignatureHeader] = signature,
        };

    [Fact]
    public void Verify_CorrectSignature_Passes()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var signature = KuaishouPlatformEventVerifier.ComputeSignature(Body, Token);

        Assert.True(verifier.Verify(PlatformNames.Kuaishou, Body, Headers(signature)));
    }

    [Fact]
    public void ComputeSignature_MatchesOfficialSha1HexAlgorithm()
    {
        using var sha1 = SHA1.Create();
        var expected = Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(Body + Token))).ToLowerInvariant();

        Assert.Equal(expected, KuaishouPlatformEventVerifier.ComputeSignature(Body, Token));
    }

    [Fact]
    public void Verify_TamperedBody_Rejected()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var signature = KuaishouPlatformEventVerifier.ComputeSignature(Body, Token);
        var tampered = Body.Replace("m-1", "m-2");

        Assert.False(verifier.Verify(PlatformNames.Kuaishou, tampered, Headers(signature)));
    }

    [Fact]
    public void Verify_WrongToken_Rejected()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var signature = KuaishouPlatformEventVerifier.ComputeSignature(Body, "other-token");

        Assert.False(verifier.Verify(PlatformNames.Kuaishou, Body, Headers(signature)));
    }

    [Fact]
    public void Verify_MissingToken_FailClosed()
    {
        var verifier = new KuaishouPlatformEventVerifier(verifyToken: null);
        var signature = KuaishouPlatformEventVerifier.ComputeSignature(Body, Token);

        Assert.False(verifier.Verify(PlatformNames.Kuaishou, Body, Headers(signature)));
    }

    [Fact]
    public void Verify_MissingSignatureHeader_Rejected()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);

        Assert.False(verifier.Verify(PlatformNames.Kuaishou, Body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Verify_AcceptsCustomSignatureHeader()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token, "X-Custom-Kwai-Signature");
        var signature = KuaishouPlatformEventVerifier.ComputeSignature(Body, Token);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Custom-Kwai-Signature"] = signature,
        };

        Assert.True(verifier.Verify(PlatformNames.Kuaishou, Body, headers));
    }

    [Fact]
    public void Verify_WrongPlatform_FailClosed()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var signature = KuaishouPlatformEventVerifier.ComputeSignature(Body, Token);

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers(signature)));
    }
}
