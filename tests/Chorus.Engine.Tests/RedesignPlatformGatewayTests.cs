using System.Security.Cryptography;
using Chorus.Redesign.Platform;

namespace Chorus.Engine.Tests;

public sealed class RedesignPlatformGatewayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(10_000);

    private static DevelopmentPlatformSimulator NewSimulator(Func<DateTimeOffset>? clock = null)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        return clock is null ? new DevelopmentPlatformSimulator(key) : new DevelopmentPlatformSimulator(key, clock);
    }

    private static RedesignPlatformGateway NewGateway(bool dev = false, DevelopmentPlatformSimulator? simulator = null)
        => new([simulator ?? NewSimulator(), new DouyinLaunchAdapter(), new KuaishouLaunchAdapter()],
            allowNotProductionReady: dev);

    private static PlatformLaunchRequest NewRequest(DevelopmentPlatformSimulator simulator, string appId = "app-1",
        string? openId = null, string? liveKey = null, TimeSpan? lifetime = null)
        => new(appId, simulator.IssueCredential(appId, openId ?? "open-1", liveKey ?? "live-1",
            lifetime ?? TimeSpan.FromMinutes(5)), "attempt-1");

    [Fact]
    public async Task SimulatorVerifiesWellFormedCredentialIntoAppScopedTrustedIdentity()
    {
        var simulator = NewSimulator(() => Now);
        var gateway = NewGateway(dev: true, simulator);
        var verified = await gateway.VerifyLaunchAsync("simulator", NewRequest(simulator, appId: "app-1"));
        Assert.Equal("simulator", verified.Platform);
        Assert.Equal("app-1", verified.AppId);
        Assert.Equal("open-1", verified.OpenId);
        Assert.Equal("live-1", verified.CanonicalLiveSessionKey);
        Assert.Equal("attempt-1", verified.LoginAttemptId);
        // The same credential verifies again: replay detection belongs to the caller's
        // LoginAttempt persistence, not the verification adapter.
        var replay = await gateway.VerifyLaunchAsync("simulator", NewRequest(simulator, appId: "app-1"));
        Assert.Equal(verified.OpenId, replay.OpenId);
    }

    [Fact]
    public async Task SimulatorRejectsForgedExpiredAndMismatchedCredentialsWithStableCodes()
    {
        var simulator = NewSimulator(() => Now);
        var gateway = NewGateway(dev: true, simulator);
        var genuine = NewRequest(simulator).LaunchCredential;

        var forged = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            gateway.VerifyLaunchAsync("simulator", new PlatformLaunchRequest("app-1", genuine[..^4] + "AAAA", "attempt-1")));
        Assert.Equal("bad-signature", forged.ErrorCode);

        var expired = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            gateway.VerifyLaunchAsync("simulator", new PlatformLaunchRequest("app-1",
                simulator.IssueCredential("app-1", "open-1", "live-1", TimeSpan.FromMinutes(-1)), "attempt-1")));
        Assert.Equal("credential-expired", expired.ErrorCode);

        var wrongApp = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            gateway.VerifyLaunchAsync("simulator", new PlatformLaunchRequest("app-2", genuine, "attempt-1")));
        Assert.Equal("app-mismatch", wrongApp.ErrorCode);

        var malformed = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            gateway.VerifyLaunchAsync("simulator", new PlatformLaunchRequest("app-1", "not-a-credential", "attempt-1")));
        Assert.Equal("malformed-credential", malformed.ErrorCode);
    }

    [Fact]
    public async Task GatewayRejectsUnknownPlatformAndMalformedRequests()
    {
        var simulator = NewSimulator();
        var gateway = NewGateway(dev: true, simulator);
        var unknown = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            gateway.VerifyLaunchAsync("douyin-real", NewRequest(simulator)));
        Assert.Equal("platform-unknown", unknown.ErrorCode);
        var emptyAttempt = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            gateway.VerifyLaunchAsync("simulator", new PlatformLaunchRequest("app-1",
                simulator.IssueCredential("app-1", "open-1", "live-1", TimeSpan.FromMinutes(5)), "")));
        Assert.Equal("malformed-credential", emptyAttempt.ErrorCode);
    }

    [Fact]
    public async Task GatewayFailsClosedOnAdaptersWithoutProductionEvidence()
    {
        var simulator = NewSimulator(() => Now);
        var production = NewGateway(dev: false, simulator);
        var simulatorRejected = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            production.VerifyLaunchAsync("simulator", NewRequest(simulator)));
        Assert.Equal("adapter-not-production-ready", simulatorRejected.ErrorCode);
        var douyinRejected = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            production.VerifyLaunchAsync("douyin", NewRequest(simulator, appId: "douyin-app")));
        Assert.Equal("adapter-not-production-ready", douyinRejected.ErrorCode);

        // Even in development mode the named platform stubs never verify a credential.
        var development = NewGateway(dev: true, simulator);
        var douyinStub = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            development.VerifyLaunchAsync("douyin", NewRequest(simulator, appId: "douyin-app")));
        Assert.Equal("adapter-not-production-ready", douyinStub.ErrorCode);
        var kuaishouStub = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            development.VerifyLaunchAsync("kuaishou", NewRequest(simulator, appId: "kuaishou-app")));
        Assert.Equal("adapter-not-production-ready", kuaishouStub.ErrorCode);
        Assert.False(simulator.Capabilities.ProductionReady);
        Assert.Contains("development-simulator", simulator.Capabilities.Evidence);
        Assert.False(new DouyinLaunchAdapter().Capabilities.ProductionReady);
        Assert.False(new KuaishouLaunchAdapter().Capabilities.ProductionReady);
    }

    [Fact]
    public async Task GatewayValidatesAdapterOutputAndNeverExposesCredentials()
    {
        // A misbehaving adapter cannot smuggle an empty identity or a foreign platform through.
        var broken = new BrokenAdapter();
        var gateway = new RedesignPlatformGateway([broken], allowNotProductionReady: true);
        var violation = await Assert.ThrowsAsync<PlatformLaunchRejectedException>(() =>
            gateway.VerifyLaunchAsync("broken", new PlatformLaunchRequest("app-1", "anything", "attempt-1")));
        Assert.Equal("adapter-violation", violation.ErrorCode);

        // VerifiedLaunch carries identity only; the raw credential is absent by construction.
        var simulator = NewSimulator(() => Now);
        var request = NewRequest(simulator);
        var devGateway = NewGateway(dev: true, simulator);
        var verified = await devGateway.VerifyLaunchAsync("simulator", request);
        Assert.DoesNotContain(request.LaunchCredential, string.Join('|', verified.Platform, verified.AppId,
            verified.OpenId, verified.CanonicalLiveSessionKey, verified.LoginAttemptId));
    }

    private sealed class BrokenAdapter : IPlatformLaunchAdapter
    {
        public string Platform => "broken";
        public PlatformLaunchCapabilities Capabilities { get; } =
            new("broken", ProductionReady: true, Evidence: "test-only-broken-adapter");
        public Task<VerifiedLaunch> VerifyLaunchAsync(PlatformLaunchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new VerifiedLaunch("simulator", "other-app", " ", " ", "attempt-1", DateTimeOffset.UtcNow));
    }
}
