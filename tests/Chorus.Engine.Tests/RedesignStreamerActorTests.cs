using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.StreamerServer;

namespace Chorus.Engine.Tests;

/// <summary>
/// StreamerActor in-memory lifecycle (architecture 5.3) and its coordinator wiring: recovery
/// failure blocks LoginReady, a restart with an empty registry re-derives committed facts, and
/// connection close/attach move the entity between Ready and RetainingOffline.
/// </summary>
public sealed class RedesignStreamerActorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20_000);

    private sealed class FakeIdentityStore : IStreamerIdentityStore
    {
        public int Logins;
        public bool FailActiveBinding;
        public StreamerSessionState State = new("streamer-fake", "tenant-a", 1, "gateway-1", "connection-1",
            1, Revoked: false, Closed: false, Expired: false, Banned: false, Current: true);
        public bool LiveSessionOpen = true;
        public StreamerBinding? Binding = new("binding-fake", "live-fake", "game-a", 1, "Ready", false);

        public Task<string?> GetSessionTenantAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<StreamerSessionRevocation> RevokeSessionAsync(string tenantId, string sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerSessionRevocation(sessionId, "streamer-fake", 1, true, null, null, 0));

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
            CancellationToken cancellationToken = default) => Task.FromResult<StreamerSessionState?>(State);

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
            => FailActiveBinding
                ? throw new InvalidOperationException("binding storage down")
                : Task.FromResult<StreamerBindingStatus?>(Binding is null
                    ? null
                    : new StreamerBindingStatus(Binding.LiveSessionId, Binding.BindingId, Binding.GameId,
                        Binding.Generation, Binding.State));
    }

    private sealed record Rig(
        RedesignStreamerLoginCoordinator Coordinator, FakeIdentityStore Store, StreamerActorRegistry Registry,
        Func<string, string> IssueCredential);

    private static Rig New()
    {
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey, () => Now);
        var store = new FakeIdentityStore();
        var registry = new StreamerActorRegistry();
        var coordinator = new RedesignStreamerLoginCoordinator(
            new RedesignPlatformGateway([simulator], allowNotProductionReady: true),
            store,
            new LoadedProfileStore(),
            registry,
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["tenant-a"] = new Dictionary<string, string> { ["app-1"] = "game-a" }
            },
            RandomNumberGenerator.GetBytes(32),
            () => Now);
        return new Rig(coordinator, store, registry,
            appId => simulator.IssueCredential(appId, "open-x", "live-x", TimeSpan.FromMinutes(5)));
    }

    private static LoginCommand Command(Func<string, string> issue) => new(
        "simulator", "app-1", issue("app-1"), "attempt-1", RandomNumberGenerator.GetBytes(32),
        "gateway-1", "connection-1");

    private sealed class LoadedProfileStore : IStreamerProfileStore
    {
        public Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task LoginRecoversBindingFactsIntoReadyActorAndSwitchUpdatesProjection()
    {
        var rig = New();
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        var actor = rig.Registry.Find("tenant-a", "streamer-fake");
        Assert.NotNull(actor);
        Assert.Equal(StreamerActorState.Ready, actor!.State);
        Assert.Equal(ready.SessionId, actor.SessionId);
        Assert.Equal(rig.Registry.FindBySession("tenant-a", ready.SessionId), actor);
        Assert.NotNull(actor.Facts);
        Assert.True(actor.Facts!.SessionOnline);
        Assert.True(actor.Facts.SessionCurrent);
        Assert.Equal(1, actor.Facts.SessionVersion);
        Assert.True(actor.Facts.LiveOpen);
        Assert.Equal("binding-fake", actor.Facts.ActiveBinding!.BindingId);
        Assert.False(actor.CanUnload); // an active binding keeps the entity in memory

        // A committed switch updates the in-memory projection of the same actor.
        await rig.Coordinator.StartGameplayAsync("tenant-a", ready.AccessCredential, "game-a", "op-1");
        Assert.Equal("game-a", actor.Facts.ActiveBinding.GameId);
        Assert.Equal(1, actor.Facts.ActiveBinding.Generation);
    }

    [Fact]
    public async Task RecoveryFailureBlocksLoginReadyAndRetryConverges()
    {
        var rig = New();
        rig.Store.FailActiveBinding = true;
        await Assert.ThrowsAsync<EntityRecoveryFailedException>(
            () => rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential)));
        Assert.Equal(0, rig.Store.Logins); // no session grant may exist when recovery failed
        var actor = rig.Registry.Find("tenant-a", "streamer-fake");
        Assert.Equal(StreamerActorState.RecoveryFailed, actor!.State);

        rig.Store.FailActiveBinding = false;
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        Assert.Equal(StreamerActorState.Ready, actor.State);
        Assert.Equal(ready.SessionId, actor.SessionId);
    }

    [Fact]
    public async Task ConnectionClosedRetainsOfflineAndAttachOrResumeRestoresReady()
    {
        var rig = New();
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        var actor = rig.Registry.Find("tenant-a", "streamer-fake")!;

        Assert.True(await rig.Coordinator.ConnectionClosedAsync("tenant-a", ready.SessionId,
            "gateway-1", "connection-1", 1));
        Assert.Equal(StreamerActorState.RetainingOffline, actor.State);
        Assert.False(actor.Facts!.SessionOnline);
        Assert.True(actor.Facts.SessionCurrent); // offline is a routing fact, not a lost session

        // Resume after the close rotates the session and makes the entity Ready again.
        var resumed = await rig.Coordinator.ResumeAsync("tenant-a", new ResumeCommand(
            ready.SessionId, "resume-op-1", RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32), "gateway-2", "connection-2"));
        Assert.Equal(StreamerActorState.Ready, actor.State);
        Assert.True(actor.Facts.SessionOnline);
        Assert.Equal(2, actor.Facts.SessionVersion);
        Assert.Equal(resumed.SessionId, actor.SessionId);

        // A later gateway close followed by an attach (no rotation) follows the same transitions.
        Assert.True(await rig.Coordinator.ConnectionClosedAsync("tenant-a", ready.SessionId,
            "gateway-2", "connection-2", 1));
        Assert.Equal(StreamerActorState.RetainingOffline, actor.State);
        await rig.Coordinator.AttachConnectionAsync("tenant-a", new AttachConnectionCommand(
            ready.SessionId, "attach-op-1", RandomNumberGenerator.GetBytes(32), "gateway-3", "connection-3"));
        Assert.Equal(StreamerActorState.Ready, actor.State);
        Assert.True(actor.Facts.SessionOnline);
    }

    [Fact]
    public async Task RestartWithEmptyRegistryRecoversCommittedFactsAndUnloadNeedsNoWork()
    {
        var rig = New();
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        await rig.Coordinator.ConnectionClosedAsync("tenant-a", ready.SessionId,
            "gateway-1", "connection-1", 1);
        // Persisted facts at crash time: the connection is marked closed in the database, and a
        // switch committed right before the crash: only the committed binding is observable.
        rig.Store.State = rig.Store.State with { Closed = true };
        rig.Store.Binding = new StreamerBinding("binding-2", "live-fake", "game-b", 2, "Ready", false);

        var restarted = new StreamerActorRegistry(); // fresh process, empty memory
        var actor = restarted.GetOrAdd("tenant-a", "streamer-fake");
        var facts = await actor.RecoverAsync(rig.Store, ready.SessionId);
        Assert.Equal(StreamerActorState.RetainingOffline, actor.State); // closed session, entity retained
        Assert.False(facts.SessionOnline);
        Assert.True(facts.SessionCurrent);
        Assert.Equal("binding-2", facts.ActiveBinding!.BindingId); // committed reality, not pre-crash memory
        Assert.Equal("game-b", facts.ActiveBinding.GameId);
        Assert.False(actor.CanUnload); // the active binding keeps the entity loaded
        Assert.Equal(actor, restarted.FindBySession("tenant-a", ready.SessionId)); // recovered session routes here

        // Teardown lands in Deactivating; unload is allowed only with no binding and no session.
        Assert.True(await actor.BeginDeactivateAsync());
        Assert.Equal(StreamerActorState.Deactivating, actor.State);
        Assert.False(actor.CanUnload); // the binding still holds the entity
        rig.Store.Binding = null;
        rig.Store.State = rig.Store.State with { Current = false };
        await actor.RecoverAsync(rig.Store, ready.SessionId);
        Assert.Equal(StreamerActorState.Deactivating, actor.State); // explicit decision survives refresh
        Assert.True(actor.CanUnload);
        Assert.True(restarted.Remove(actor));
    }
}
