using System.Security.Cryptography;
using System.Text;
using Chorus.Adapters.Platforms;
using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Load-tool signing coverage: the helper must produce the documented
/// placeholder scheme (HMAC-SHA256(secret, rawBody) as lowercase hex) that the
/// gateway's Douyin verifier checks. Expected values are hardcoded vectors
/// computed independently (python3 hmac/hashlib), not round-trips through the
/// helper, so a regression in the construction is caught.
/// </summary>
public sealed class IngressSignerTests
{
    private const string Secret = "test-secret";
    private const string Body = """{"event":"comment","event_id":"dy-1","session_id":"s1","user_id":"u1"}""";
    private const string Expected = "8d3ac7dee14cfddc255a450a7e3416802a2d2b5deff4b83dadedc2344cf3d507";

    [Fact]
    public void ComputeSignature_KnownVector_MatchesDocumentedScheme()
    {
        Assert.Equal(Expected, IngressSigner.ComputeSignature(Body, Secret));
    }

    [Fact]
    public void ComputeSignature_SecondVector_MatchesDocumentedScheme()
    {
        // Second vector (also computed with python3 hmac/hashlib) so the vector
        // table is not a single lucky coincidence.
        const string body = """{"session_id":"load-s1","event_id":"evt-abc","user_id":"u42","event":"comment","match_id":null}""";
        const string expected = "e8c4edfb46d1d15699150cc0d1efe17f8b715e6a3d2b0d5c4d8fceac46b79135";

        Assert.Equal(expected, IngressSigner.ComputeSignature(body, "loadtool-signing-key"));
    }

    [Fact]
    public void ComputeSignature_IsLowercaseHex()
    {
        var signature = IngressSigner.ComputeSignature(Body, Secret);

        Assert.Equal(64, signature.Length);
        Assert.Matches("^[0-9a-f]{64}$", signature);
    }

    [Theory]
    [InlineData("dy-2")]
    [InlineData("s2")]
    public void ComputeSignature_ChangesWhenBodyChanges(string tamperedField)
    {
        Assert.NotEqual(Expected, IngressSigner.ComputeSignature(Body.Replace("dy-1", tamperedField), Secret));
    }

    [Fact]
    public void ComputeSignature_ChangesWhenSecretChanges()
    {
        Assert.NotEqual(Expected, IngressSigner.ComputeSignature(Body, "other-secret"));
    }

    [Fact]
    public void ComputeSignature_MatchesGatewayVerifierConstruction()
    {
        Assert.Equal(
            DouyinPlatformEventVerifier.ComputeSignature(Body, Secret),
            IngressSigner.ComputeSignature(Body, Secret));
    }

    [Fact]
    public void SignatureHeader_IsGatewayDefaultHeader()
    {
        Assert.Equal(DouyinPlatformEventVerifier.DefaultSignatureHeader, IngressSigner.SignatureHeader);
    }

    [Fact]
    public void SignedBody_PassesGatewayVerifier()
    {
        // End-to-end guarantee for this feature: the load tool's signature over
        // the body it posts is accepted by the fail-closed gateway verifier.
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [IngressSigner.SignatureHeader] = IngressSigner.ComputeSignature(Body, Secret),
        };

        Assert.True(verifier.Verify(PlatformNames.Douyin, Body, headers));
    }
}
