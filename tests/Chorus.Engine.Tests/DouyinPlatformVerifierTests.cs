using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

public sealed class DouyinPlatformVerifierTests
{
    private const string Secret = "test-secret";
    private const string Body = """{"event":"comment","event_id":"dy-1","session_id":"s1","user_id":"u1"}""";

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string FreshBody(string eventId = "dy-1")
        => $$"""{"event":"comment","event_id":"{{eventId}}","timestamp":{{NowMs}},"session_id":"s1","user_id":"u1"}""";

    // A body whose timestamp sits offsetMs away from now (negative = past).
    private static string OffsetBody(long offsetMs)
        => $$"""{"event":"comment","event_id":"dy-1","timestamp":{{NowMs + offsetMs}},"session_id":"s1","user_id":"u1"}""";

    private static IReadOnlyDictionary<string, string> Headers(string signature)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DouyinPlatformEventVerifier.DefaultSignatureHeader] = signature,
        };

    private static string Sign(string body) => DouyinPlatformEventVerifier.ComputeSignature(body, Secret);

    [Fact]
    public void Verify_CorrectSignature_Passes()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var body = FreshBody();

        Assert.True(verifier.Verify(PlatformNames.Douyin, body, Headers(Sign(body))));
    }

    // Freshness (review M5): the timestamp rides inside the signed body; a valid
    // signature over an out-of-window or absent timestamp is still refused.
    [Fact]
    public void Verify_StaleTimestamp_Rejected()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var body = OffsetBody(-400_000);

        Assert.False(verifier.Verify(PlatformNames.Douyin, body, Headers(Sign(body))));
    }

    [Fact]
    public void Verify_FutureTimestamp_Rejected()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var body = OffsetBody(400_000);

        Assert.False(verifier.Verify(PlatformNames.Douyin, body, Headers(Sign(body))));
    }

    [Fact]
    public void Verify_MissingTimestamp_Rejected()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);

        Assert.False(verifier.Verify(PlatformNames.Douyin, Body, Headers(Sign(Body))));
    }

    [Fact]
    public void Verify_NonNumericTimestamp_Rejected()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var body = """{"event":"comment","event_id":"dy-1","timestamp":"not-a-number","session_id":"s1","user_id":"u1"}""";

        Assert.False(verifier.Verify(PlatformNames.Douyin, body, Headers(Sign(body))));
    }

    [Fact]
    public void Verify_TamperedTimestamp_Rejected()
    {
        // An attacker cannot refresh the timestamp and keep the captured signature:
        // the field is inside the signed body, so the replayed signature no longer
        // matches even though the refreshed timestamp would pass the freshness bound.
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var captured = OffsetBody(-400_000);
        var capturedSignature = Sign(captured);
        var refreshed = FreshBody();

        Assert.NotEqual(captured, refreshed);
        Assert.False(verifier.Verify(PlatformNames.Douyin, refreshed, Headers(capturedSignature)));
    }

    [Fact]
    public void Verify_ToleranceOverride_WidensTheAcceptedWindow()
    {
        // The default verifier refuses a 400s-old payload; a verifier configured
        // with a 3600s tolerance accepts exactly that payload.
        var body = OffsetBody(-400_000);
        var headers = Headers(Sign(body));

        Assert.False(new DouyinPlatformEventVerifier(Secret).Verify(PlatformNames.Douyin, body, headers));
        Assert.True(new DouyinPlatformEventVerifier(Secret, timestampToleranceSeconds: 3600)
            .Verify(PlatformNames.Douyin, body, headers));
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
        var verifier = new DouyinPlatformEventVerifier(Secret, signatureHeader: "X-Custom-Signature");
        var body = FreshBody();
        var signature = DouyinPlatformEventVerifier.ComputeSignature(body, Secret);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Custom-Signature"] = signature,
        };

        Assert.True(verifier.Verify(PlatformNames.Douyin, body, headers));
        Assert.False(verifier.Verify(PlatformNames.Douyin, body, Headers(signature)));
    }

    [Fact]
    public void Verify_AcceptsUppercaseHexSignature()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);
        var body = FreshBody();
        var signature = DouyinPlatformEventVerifier.ComputeSignature(body, Secret).ToUpperInvariant();

        Assert.True(verifier.Verify(PlatformNames.Douyin, body, Headers(signature)));
    }

    // The secret proves nothing nameable, so the trusted app identity (review C1) exists
    // only when Ingress:Douyin:AppId is configured — then the ingress binds the
    // X-Chorus-App-Id header to it.
    [Fact]
    public void TrustedAppId_ConfiguredAppId_IsExposed()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret, appId: "  app-dy  ");

        Assert.Equal("app-dy", verifier.GetTrustedAppId(PlatformNames.Douyin));
    }

    [Fact]
    public void TrustedAppId_UnconfiguredAppId_ClaimsNothing()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret);

        Assert.Null(verifier.GetTrustedAppId(PlatformNames.Douyin));
    }

    [Fact]
    public void TrustedAppId_OtherPlatform_ClaimsNothing()
    {
        var verifier = new DouyinPlatformEventVerifier(Secret, appId: "app-dy");

        Assert.Null(verifier.GetTrustedAppId(PlatformNames.Kuaishou));
    }
}
