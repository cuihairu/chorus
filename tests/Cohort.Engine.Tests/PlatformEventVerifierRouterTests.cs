using Cohort.Adapters.Abstractions;
using Cohort.Adapters.Platforms;

namespace Cohort.Engine.Tests;

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

    private static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Verify_NamedPlatform_DispatchesToPlatformVerifier()
    {
        var douyin = new RejectAllVerifier(PlatformNames.Douyin, result: true);
        var router = new PlatformEventVerifierRouter(new IPlatformNamedEventVerifier[] { douyin });

        Assert.True(router.Verify(PlatformNames.Douyin, "{}", Headers));
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
}
