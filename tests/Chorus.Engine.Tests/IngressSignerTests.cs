using System.Text.RegularExpressions;
using Chorus.Adapters.Platforms;
using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Load-tool signing helper coverage with vectors computed independently of
/// both the helper and the gateway verifiers (python3 hmac/hashlib), so a
/// regression in either construction is caught. Parity with the gateway
/// construction is asserted directly against the verifier code.
/// </summary>
public sealed class IngressSignerTests
{
    private const string Secret = "test-secret";
    private const string Body = """{"event":"comment","event_id":"dy-1","session_id":"s1","user_id":"u1"}""";
    private const string Expected = "8d3ac7dee14cfddc255a450a7e3416802a2d2b5deff4b83dadedc2344cf3d507";

    [Fact]
    public void Sign_Douyin_KnownVector_MatchesDocumentedScheme()
    {
        var signature = IngressSigner.Sign("douyin", Body, Secret);

        Assert.Equal(DouyinPlatformEventVerifier.DefaultSignatureHeader, signature!.Value.Name);
        Assert.Equal(Expected, signature.Value.Value);
    }

    [Fact]
    public void Sign_Douyin_PassesTheGatewayVerifier()
    {
        var signature = IngressSigner.Sign("douyin", Body, Secret)!;
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [signature.Value.Name] = signature.Value.Value,
        };

        // The contract that matters: what the tool signs, the gateway accepts.
        Assert.True(verifier.Verify(PlatformNames.Douyin, Body, headers));
    }

    [Fact]
    public void Sign_Kuaishou_PassesTheGatewayVerifier()
    {
        var signature = IngressSigner.Sign("kuaishou", Body, Secret)!;

        var verifier = new KuaishouPlatformEventVerifier(Secret);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [signature.Value.Name] = signature.Value.Value,
        };

        Assert.Equal("kwaisign", signature.Value.Name);
        Assert.True(verifier.Verify(PlatformNames.Kuaishou, Body, headers));
    }

    [Fact]
    public void Sign_Douyin_KnownVector_IsHmacSha256OfRawBody()
    {
        // Independent recomputation of the documented placeholder construction
        // (HMAC-SHA256 of the raw body, lowercase hex) inside the test.
        var expected = Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Secret),
                System.Text.Encoding.UTF8.GetBytes(Body))).ToLowerInvariant();

        Assert.Equal(expected, IngressSigner.Sign("douyin", Body, Secret)!.Value.Value);
    }

    [Fact]
    public void Sign_Kuaishou_KnownVector_IsSha1OfBodyPlusToken()
    {
        // Official Kuaishou construction: sha1Hex(rawBody + token), lowercase.
        const string expected = "1d9e41c01faf769dd954e8e6f08ab12b7de4feaf";

        Assert.Equal(expected, IngressSigner.Sign("kuaishou", Body, Secret)!.Value.Value);
    }

    [Fact]
    public void Sign_SignatureIsLowercaseHex()
    {
        var signature = IngressSigner.Sign("douyin", Body, Secret)!.Value.Value;

        Assert.Matches("^[0-9a-f]{64}$", signature);
    }

    [Fact]
    public void Sign_IsCaseInsensitiveOnPlatformName()
    {
        Assert.Equal(Expected, IngressSigner.Sign("DOUYIN", Body, Secret)!.Value.Value);
        Assert.True(IngressSigner.CanSign("Douyin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sign_BlankSecret_ReturnsNoHeader(string? secret)
    {
        // Same normalization the gateway verifiers apply: no secret means
        // nothing to sign with, and an empty signature would be worse than none.
        Assert.Null(IngressSigner.Sign("douyin", Body, secret));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("bilibili")]
    [InlineData("anything-else")]
    public void Sign_PlatformWithoutSigner_ReturnsNoHeader(string platform)
    {
        // The tool refuses to invent a scheme it cannot verify against the
        // gateway's own code (Bilibili's is a different construction, the test
        // platform is AllowAll).
        Assert.False(IngressSigner.CanSign(platform));
        Assert.Null(IngressSigner.Sign(platform, Body, Secret));
    }

    [Fact]
    public void CanSign_MatchesTheSignedPlatforms()
    {
        Assert.True(IngressSigner.CanSign("douyin"));
        Assert.True(IngressSigner.CanSign("kuaishou"));
        Assert.False(IngressSigner.CanSign("test"));
    }

    [Theory]
    [InlineData("dy-2")]
    [InlineData("s2")]
    public void Sign_Douyin_ChangesWhenBodyChanges(string tamperedField)
    {
        Assert.NotEqual(Expected, IngressSigner.Sign("douyin", Body.Replace("dy-1", tamperedField), Secret)!.Value.Value);
    }

    [Fact]
    public void Sign_Douyin_ChangesWhenSecretChanges()
    {
        Assert.NotEqual(Expected, IngressSigner.Sign("douyin", Body, "other-secret")!.Value.Value);
    }
}
