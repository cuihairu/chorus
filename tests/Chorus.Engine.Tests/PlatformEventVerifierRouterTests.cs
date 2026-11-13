using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

public sealed class PlatformEventVerifierRouterTests
{
    private sealed class RejectAllVerifier : IPlatformNamedEventVerifier
    {
        public string PlatformName { get; }
        public bool Result { get; init; }

        public RejectAllVerifier(string platformName, bool result)
        {
            PlatformName = platformName;
            Result = result;
        }

        public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers) => Result;
    }

    private sealed class IdentityVerifier : IPlatformNamedEventVerifier, IPlatformTrustedAppIdSource
    {
        public IdentityVerifier(string platformName, string? trustedAppId)
        {
            PlatformName = platformName;
            TrustedAppId = trustedAppId;
        }

        public string PlatformName { get; }
        public string? TrustedAppId { get; }

        public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers) => true;

        public string? GetTrustedAppId(string platform)
            => string.Equals(platform, PlatformName, StringComparison.OrdinalIgnoreCase) ? TrustedAppId : null;
    }

    private static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Verify_NamedPlatform_DispatchesToPlatformVerifier()
    {
        var douyin = new RejectAllVerifier(PlatformNames.Douyin, result: true);
        var router = new PlatformEventVerifierRouter(new IPlatformNamedEventVerifier[] { douyin });

        Assert.True(router.Verify(PlatformNames.Douyin, "{}", Headers));
    }

    // Duplicate named registrations are refused at construction (review M6): last-wins
    // dictionary writes would silently replace one implementation — possibly the real
    // verifying one — with whichever registered later. Case differences collide too.
    [Fact]
    public void ADuplicateNamedVerifierRegistration_IsAConstructionError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[]
            {
                new RejectAllVerifier(PlatformNames.Douyin, result: true),
                new RejectAllVerifier(PlatformNames.Douyin, result: false),
            }));

        Assert.Contains("'douyin'", error.Message);
        Assert.Contains("RejectAllVerifier", error.Message);
        Assert.Contains("duplicate named verifier registrations", error.Message);
    }

    [Fact]
    public void ADuplicateNamedVerifierRegistration_IsCaseInsensitive()
    {
        Assert.Throws<InvalidOperationException>(() => new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[]
            {
                new RejectAllVerifier("douyin", result: true),
                new RejectAllVerifier("Douyin", result: false),
            }));
    }

    [Fact]
    public void Verify_PlatformVerifierRejects_Fails()
    {
        var douyin = new RejectAllVerifier(PlatformNames.Douyin, result: false);
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { douyin },
            fallback: new AllowAllPlatformEventVerifier());

        Assert.False(router.Verify(PlatformNames.Douyin, "{}", Headers));
    }

    [Fact]
    public void Verify_UnknownPlatform_UsesFallback()
    {
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { new RejectAllVerifier(PlatformNames.Douyin, result: true) },
            fallback: new AllowAllPlatformEventVerifier());

        Assert.True(router.Verify(PlatformNames.Test, "{}", Headers));
        Assert.True(router.Verify(PlatformNames.Simulator, "{}", Headers));
    }

    [Fact]
    public void Verify_FallbackIsBoundToTheDevelopmentIdentities()
    {
        // 开发 fallback 只应答 test/simulator 身份（review 2026-11-13 M3）：新 mapper
        // 漏配 verifier 的注册缺口不再被 fallback 静默放行——未知平台一律拒绝。
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { new RejectAllVerifier(PlatformNames.Douyin, result: true) },
            fallback: new AllowAllPlatformEventVerifier());

        Assert.False(router.Verify("some-future-platform", "{}", Headers));
        Assert.False(router.Verify(PlatformNames.Kuaishou, "{}", Headers));
        Assert.False(router.Verify("", "{}", Headers));
    }

    [Fact]
    public void Verify_UnknownPlatform_NoFallback_FailClosed()
    {
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { new RejectAllVerifier(PlatformNames.Douyin, result: true) });

        Assert.False(router.Verify(PlatformNames.Test, "{}", Headers));
    }

    [Fact]
    public void Verify_EmptyPlatform_NoFallback_FailClosed()
    {
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { new RejectAllVerifier(PlatformNames.Douyin, result: true) });

        Assert.False(router.Verify("", "{}", Headers));
    }

    // The trusted app identity (review C1) is the mounted verifier's claim about the
    // credentials it holds; the router surfaces whichever verifier answers the platform.
    [Fact]
    public void TrustedAppId_NamedVerifier_SurfacesItsClaim()
    {
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { new IdentityVerifier(PlatformNames.Douyin, "app-dy") });

        Assert.Equal("app-dy", router.GetTrustedAppId(PlatformNames.Douyin));
        // A platform the claiming verifier does not answer has no claim of its own.
        Assert.Null(router.GetTrustedAppId(PlatformNames.Kuaishou));
        Assert.Null(router.GetTrustedAppId("unknown"));
    }

    [Fact]
    public void TrustedAppId_NamedClaimOfNone_IsAuthoritative_OverFallback()
    {
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { new IdentityVerifier(PlatformNames.Douyin, trustedAppId: null) },
            fallback: new IdentityVerifier(PlatformNames.Douyin, "app-fallback"));

        // The named verifier answered the platform and named no identity; the dev
        // fallback's claim must not resurrect one behind it.
        Assert.Null(router.GetTrustedAppId(PlatformNames.Douyin));
    }

    [Fact]
    public void TrustedAppId_PlainNamedVerifier_FallsBackToFallbackClaim()
    {
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { new RejectAllVerifier(PlatformNames.Douyin, result: true) },
            fallback: new IdentityVerifier(PlatformNames.Douyin, "app-fallback"));

        // The named verifier is not an identity source, so the fallback's claim for the
        // same platform is what the router surfaces.
        Assert.Equal("app-fallback", router.GetTrustedAppId(PlatformNames.Douyin));
    }

    [Fact]
    public void TrustedAppId_NoNamedNoFallback_Null()
    {
        var router = new PlatformEventVerifierRouter(
            new IPlatformNamedEventVerifier[] { new IdentityVerifier(PlatformNames.Douyin, "app-dy") });

        Assert.Null(router.GetTrustedAppId(PlatformNames.Test));
        Assert.Null(router.GetTrustedAppId(""));
    }
}
