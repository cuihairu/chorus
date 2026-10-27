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
        public StreamerSessionState? State = new("streamer-fake", "tenant-a", 1, "gateway-1", "connection-1",
            1, Revoked: false, Closed: false, Expired: false, Banned: false, Current: true);

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

        public Task<StreamerConnection> AttachConnectionAsync(string tenantId, string sessionId,
            string gatewayInstanceId, string connectionId, string operationId, byte[] currentSecret,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerConnection(sessionId, "streamer-fake", 1, 2, false));

        public Task<bool> ConnectionClosedAsync(string tenantId, string sessionId, string gatewayInstanceId,
            string connectionId, long connectionGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<StreamerSessionState?> GetSessionStateAsync(string tenantId, string sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult(State);

        public bool LiveSessionOpen = true;
        public StreamerBinding? Binding = new("binding-fake", "live-fake", "game-a", 1, "Ready", false);

        public Task<string> OpenLiveSessionAsync(string tenantId, string streamerId, string platform, string appId,
            string platformRoomKey, string canonicalLiveSessionKey, CancellationToken cancellationToken = default)
            => Task.FromResult("live-fake");

        public Task<string?> GetLiveSessionIdAsync(string tenantId, string streamerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(LiveSessionOpen ? "live-fake" : null);

        public Task<StreamerBinding> StartGameplayAsync(string tenantId, string liveSessionId, string sessionId,
            string gameId, string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(Binding! with { GameId = gameId });

        public Task<StreamerBindingStatus?> GetActiveBindingAsync(string tenantId, string streamerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StreamerBindingStatus?>(Binding is null
                ? null
                : new StreamerBindingStatus(Binding.LiveSessionId, Binding.BindingId, Binding.GameId,
                    Binding.Generation, Binding.State));
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
            new StreamerActorRegistry(),
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
    public async Task AttachConnectionPassesThroughWithoutRotation()
    {
        var rig = New();
        var connection = await rig.Coordinator.AttachConnectionAsync("tenant-a", new AttachConnectionCommand(
            "session-fake", "attach-op-1", RandomNumberGenerator.GetBytes(32), "gateway-2", "connection-2"));
        Assert.Equal(2, connection.ConnectionGeneration);
        Assert.True(await rig.Coordinator.ConnectionClosedAsync("tenant-a", "session-fake",
            "gateway-2", "connection-2", 2));
    }

    [Fact]
    public async Task ValidateAccessBindsCredentialToCurrentSessionState()
    {
        var rig = New();
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        var access = await rig.Coordinator.ValidateAccessAsync("tenant-a", ready.AccessCredential);
        Assert.True(access.Online);
        Assert.Equal(1, access.SessionVersion);

        await Assert.ThrowsAsync<AccessCredentialRejectedException>(
            () => rig.Coordinator.ValidateAccessAsync("tenant-a", "forged"));
        await Assert.ThrowsAsync<AccessCredentialRejectedException>(
            () => rig.Coordinator.ValidateAccessAsync("tenant-b", ready.AccessCredential));
        rig.Store.State = rig.Store.State with { Current = false };
        await Assert.ThrowsAsync<AccessCredentialRejectedException>(
            () => rig.Coordinator.ValidateAccessAsync("tenant-a", ready.AccessCredential));
        // Offline is a routing fact, not an authentication failure.
        rig.Store.State = rig.Store.State with { Current = true, Closed = true };
        var offline = await rig.Coordinator.ValidateAccessAsync("tenant-a", ready.AccessCredential);
        Assert.False(offline.Online);
    }

    [Fact]
    public async Task StartGameplayRequiresMappedGameLiveSessionAndCurrentAccess()
    {
        var rig = New();
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        var binding = await rig.Coordinator.StartGameplayAsync("tenant-a", ready.AccessCredential, "game-a", "op-1");
        Assert.Equal("live-fake", binding.LiveSessionId);
        Assert.False(binding.Duplicate);

        // Unmapped games are denied before anything else.
        await Assert.ThrowsAsync<TenantApplicationDeniedException>(
            () => rig.Coordinator.StartGameplayAsync("tenant-a", ready.AccessCredential, "game-other", "op-2"));
        await Assert.ThrowsAsync<AccessCredentialRejectedException>(
            () => rig.Coordinator.StartGameplayAsync("tenant-a", "forged", "game-a", "op-3"));
        rig.Store.LiveSessionOpen = false;
        await Assert.ThrowsAsync<BindingDeniedException>(
            () => rig.Coordinator.StartGameplayAsync("tenant-a", ready.AccessCredential, "game-a", "op-4"));

        // Status restores the active binding, or reports nothing when none is mounted.
        var status = await rig.Coordinator.GetGameplayStatusAsync("tenant-a", ready.AccessCredential);
        Assert.NotNull(status);
        Assert.Equal("binding-fake", status!.BindingId);
        rig.Store.Binding = null;
        Assert.Null(await rig.Coordinator.GetGameplayStatusAsync("tenant-a", ready.AccessCredential));
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
