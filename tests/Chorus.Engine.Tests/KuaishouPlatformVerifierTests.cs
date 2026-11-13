using System.Security.Cryptography;
using System.Text;
using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

public sealed class KuaishouPlatformVerifierTests
{
    private const string Token = "ks-verify-token";
    private const string Body = """{"encryptedMsg":"abc","msgId":"m-1","componentAppId":"ks-app","timestamp":1700000000000}""";

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string FreshBody()
        => $$"""{"encryptedMsg":"abc","msgId":"m-1","componentAppId":"ks-app","timestamp":{{NowMs}}}""";

    // An envelope whose outer timestamp sits offsetMs away from now (negative = past).
    private static string OffsetBody(long offsetMs)
        => $$"""{"encryptedMsg":"abc","msgId":"m-1","componentAppId":"ks-app","timestamp":{{NowMs + offsetMs}}}""";

    private static IReadOnlyDictionary<string, string> Headers(string signature)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [KuaishouPlatformEventVerifier.DefaultSignatureHeader] = signature,
        };

    private static string Sign(string body) => KuaishouPlatformEventVerifier.ComputeSignature(body, Token);

    [Fact]
    public void Verify_CorrectSignature_Passes()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var body = FreshBody();

        Assert.True(verifier.Verify(PlatformNames.Kuaishou, body, Headers(Sign(body))));
    }

    // Freshness (review M5): the outer envelope timestamp is inside the sha1-covered
    // body; a valid signature over an out-of-window or absent timestamp is still refused.
    [Fact]
    public void Verify_StaleTimestamp_Rejected()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var body = OffsetBody(-400_000);

        Assert.False(verifier.Verify(PlatformNames.Kuaishou, body, Headers(Sign(body))));
    }

    [Fact]
    public void Verify_FutureTimestamp_Rejected()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var body = OffsetBody(400_000);

        Assert.False(verifier.Verify(PlatformNames.Kuaishou, body, Headers(Sign(body))));
    }

    [Fact]
    public void Verify_MissingTimestamp_Rejected()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var body = """{"encryptedMsg":"abc","msgId":"m-1","componentAppId":"ks-app"}""";

        Assert.False(verifier.Verify(PlatformNames.Kuaishou, body, Headers(Sign(body))));
    }

    [Fact]
    public void Verify_TamperedTimestamp_Rejected()
    {
        // Refreshing the captured envelope's timestamp keeps it inside the freshness
        // window but breaks the captured sha1 signature - the bound is signature-protected.
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var captured = OffsetBody(-400_000);
        var capturedSignature = Sign(captured);
        var refreshed = FreshBody();

        Assert.NotEqual(captured, refreshed);
        Assert.False(verifier.Verify(PlatformNames.Kuaishou, refreshed, Headers(capturedSignature)));
    }

    [Fact]
    public void Verify_ToleranceOverride_WidensTheAcceptedWindow()
    {
        // The default verifier refuses a 400s-old envelope; a verifier configured
        // with a 3600s tolerance accepts exactly that envelope.
        var body = OffsetBody(-400_000);
        var headers = Headers(Sign(body));

        Assert.False(new KuaishouPlatformEventVerifier(Token).Verify(PlatformNames.Kuaishou, body, headers));
        Assert.True(new KuaishouPlatformEventVerifier(Token, timestampToleranceSeconds: 3600)
            .Verify(PlatformNames.Kuaishou, body, headers));
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
        var verifier = new KuaishouPlatformEventVerifier(Token, signatureHeader: "X-Custom-Kwai-Signature");
        var body = FreshBody();
        var signature = KuaishouPlatformEventVerifier.ComputeSignature(body, Token);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Custom-Kwai-Signature"] = signature,
        };

        Assert.True(verifier.Verify(PlatformNames.Kuaishou, body, headers));
    }

    [Fact]
    public void Verify_WrongPlatform_FailClosed()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);
        var signature = KuaishouPlatformEventVerifier.ComputeSignature(Body, Token);

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers(signature)));
    }

    // The verify token proves nothing nameable, so the trusted app identity (review C1)
    // exists only when Ingress:Kuaishou:AppId is configured — then the ingress binds the
    // X-Chorus-App-Id header to it.
    [Fact]
    public void TrustedAppId_ConfiguredAppId_IsExposed()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token, appId: "app-ks");

        Assert.Equal("app-ks", verifier.GetTrustedAppId(PlatformNames.Kuaishou));
    }

    [Fact]
    public void TrustedAppId_UnconfiguredAppId_ClaimsNothing()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token);

        Assert.Null(verifier.GetTrustedAppId(PlatformNames.Kuaishou));
    }

    [Fact]
    public void TrustedAppId_OtherPlatform_ClaimsNothing()
    {
        var verifier = new KuaishouPlatformEventVerifier(Token, appId: "app-ks");

        Assert.Null(verifier.GetTrustedAppId(PlatformNames.Douyin));
    }
}
