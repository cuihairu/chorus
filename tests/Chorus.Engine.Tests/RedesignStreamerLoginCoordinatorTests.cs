using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Chorus.StreamerServer;

namespace Chorus.Engine.Tests;

public sealed class RedesignStreamerLoginCoordinatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20_000);

    private sealed class FakeIdentityStore : IStreamerIdentityStore
    {
        public int Logins;
        public Task<string> ResolveIdentityAsync(string tenantId, string platform, string appId, string openId,
            CancellationToken cancellationToken = default) => Task.FromResult("streamer-fake");

        public Task<StreamerSessionGrant> LoginAsync(string tenantId, string streamerId, string gatewayInstanceId,
            string connectionId, byte[] nextResumeSecret, CancellationToken cancellationToken = default)
        {
            Logins++;
            return Task.FromResult(new StreamerSessionGrant(streamerId, "session-fake", 1,
                Now.UtcDateTime.AddHours(1), false));
        }

        public Task<StreamerSessionGrant> ResumeAsync(string tenantId, string previousSessionId, string gatewayInstanceId,
            string connectionId, string operationId, byte[] previousSecret, byte[] nextSecret,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerSessionGrant("streamer-fake", "session-fake", 2,
                Now.UtcDateTime.AddHours(1), false));
    }

    private sealed class LoadedProfileStore : IStreamerProfileStore
    {
        public Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BrokenProfileStore : IStreamerProfileStore
    {
        public Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("storage down");
    }

    private sealed record Rig(
        RedesignStreamerLoginCoordinator Coordinator, FakeIdentityStore Store,
        Func<string, string> IssueCredential);

    private static Rig New(IStreamerProfileStore? profile = null)
    {
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey, () => Now);
        var store = new FakeIdentityStore();
        var coordinator = new RedesignStreamerLoginCoordinator(
            new RedesignPlatformGateway([simulator], allowNotProductionReady: true),
            store,
            profile ?? new LoadedProfileStore(),
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["tenant-a"] = new Dictionary<string, string> { ["app-1"] = "game-a" }
            },
            RandomNumberGenerator.GetBytes(32),
            () => Now);
        return new Rig(coordinator, store,
            appId => simulator.IssueCredential(appId, "open-x", "live-x", TimeSpan.FromMinutes(5)));
    }

    private static LoginCommand Command(Func<string, string> issue, string appId = "app-1") => new(
        "simulator", appId, issue(appId), "attempt-1", RandomNumberGenerator.GetBytes(32), "gateway-1", "connection-1");

    [Fact]
    public async Task LoginReturnsReadyWithValidatedAccessCredential()
    {
        var rig = New();
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        Assert.Equal("streamer-fake", ready.StreamerId);
        Assert.Equal(1, ready.SessionVersion);
        Assert.True(ready.AccessExpiresAtUtc > Now);
    }

    [Fact]
    public async Task EntityLoadFailureBlocksLoginReadyAndSessionCreation()
    {
        var rig = New(profile: new BrokenProfileStore());
        await Assert.ThrowsAsync<EntityNotReadyException>(
            () => rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential)));
        Assert.Equal(0, rig.Store.Logins); // no session grant may exist when the entity is not ready
    }

    [Fact]
    public async Task UnmappedApplicationAndForeignTenantAreDenied()
    {
        var rig = New();
        await Assert.ThrowsAsync<TenantApplicationDeniedException>(
            () => rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential, "app-other")));
        await Assert.ThrowsAsync<TenantApplicationDeniedException>(
            () => rig.Coordinator.LoginAsync("tenant-b", Command(rig.IssueCredential)));
    }

    [Fact]
    public async Task ForgedLaunchCredentialIsRejected()
    {
        var rig = New();
        var command = Command(rig.IssueCredential) with { LaunchCredential = "forged" };
        await Assert.ThrowsAsync<PlatformLaunchRejectedException>(
            () => rig.Coordinator.LoginAsync("tenant-a", command));
    }

    [Fact]
    public void AccessCredentialExpiryAndTamperAreRejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var issued = ChorusAccessCredential.Issue(key, "s", "sess", 3, Now.AddMinutes(30), () => Now);
        Assert.Equal(("s", "sess", 3L), ChorusAccessCredential.Validate(key, issued, () => Now));
        Assert.Null(ChorusAccessCredential.Validate(key, issued, () => Now.AddMinutes(31)));
        Assert.Null(ChorusAccessCredential.Validate(key, issued[..^4] + "AAAA", () => Now));
        Assert.Null(ChorusAccessCredential.Validate(RandomNumberGenerator.GetBytes(32), issued, () => Now));
        Assert.Null(ChorusAccessCredential.Validate(key, "garbage", () => Now));
        Assert.Throws<ArgumentException>(
            () => ChorusAccessCredential.Issue(key, "s", "sess", 3, Now.AddMinutes(-1), () => Now));
    }
}
