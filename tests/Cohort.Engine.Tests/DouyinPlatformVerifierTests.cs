using Cohort.Adapters.Platforms;

namespace Cohort.Engine.Tests;

public sealed class DouyinPlatformVerifierTests
{
    private const string Secret = "test-secret";
    private const string Body = """{"event":"comment","event_id":"dy-1","session_id":"s1","user_id":"u1"}""";

    private static IReadOnlyDictionary<string, string> Headers(string signature)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DouyinPlatformEventVerifier.DefaultSignatureHeader] = signature,
        };

    [Fact]
    public void Verify_CorrectSignature_Passes()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var signature = DouyinPlatformEventVerifier.ComputeSignature(Body, Secret);

        Assert.True(verifier.Verify(PlatformNames.Douyin, Body, Headers(signature)));
    }

    [Theory]
    [InlineData("dy-2")]
    [InlineData("s2")]
    public void Verify_TamperedBody_Rejected(string tamperedField)
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var signature = DouyinPlatformEventVerifier.ComputeSignature(Body, Secret);
        var tampered = Body.Replace("dy-1", tamperedField);

        Assert.False(verifier.Verify(PlatformNames.Douyin, tampered, Headers(signature)));
    }

    [Fact]
    public void Verify_WrongSecret_Rejected()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var signature = DouyinPlatformEventVerifier.ComputeSignature(Body, "other-secret");

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers(signature)));
    }

    [Fact]
    public void Verify_MissingSecret_FailClosed()
    {
        var verifier = new DouyinPlatformEventVerifier(secret: null);
        var signature = DouyinPlatformEventVerifier.ComputeSignature(Body, Secret);

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers(signature)));
    }

    [Fact]
    public void Verify_EmptySecret_FailClosed()
    {
        var verifier = new DouyinPlatformEventVerifier("   ");

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers("0".PadRight(64, '0'))));
    }

    [Fact]
    public void Verify_MissingSignatureHeader_Rejected()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_EmptySignatureHeader_Rejected(string signature)
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers(signature)));
    }

    [Fact]
    public void Verify_NonHexSignature_Rejected()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers("not-a-hex-signature")));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("kuaishou")]
    public void Verify_WrongPlatform_FailClosed(string platform)
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var signature = DouyinPlatformEventVerifier.ComputeSignature(Body, Secret);

        Assert.False(verifier.Verify(platform, Body, Headers(signature)));
    }

    [Fact]
    public void Verify_AcceptsCustomSignatureHeader()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret, "X-Custom-Signature");
        var signature = DouyinPlatformEventVerifier.ComputeSignature(Body, Secret);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Custom-Signature"] = signature,
        };

        Assert.True(verifier.Verify(PlatformNames.Douyin, Body, headers));
        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers(signature)));
    }

    [Fact]
    public void Verify_AcceptsUppercaseHexSignature()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var signature = DouyinPlatformEventVerifier.ComputeSignature(Body, Secret).ToUpperInvariant();

        Assert.True(verifier.Verify(PlatformNames.Douyin, Body, Headers(signature)));
    }
}
