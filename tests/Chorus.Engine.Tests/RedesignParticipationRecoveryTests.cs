using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.StreamerServer;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-LINK-05: the StreamerServer recovers a lost participation state by querying the committed
/// binding (architecture 7.2 step 6「通知丢失可查询当前参与关系」). Login and resume already
/// re-derive facts; this file covers the two windows that previously left the entity absent — an
/// attach and a mount that arrive on an instance that never re-derived the entity (a process
/// restart whose gateway reconnected without a resume). The query is the identity store's
/// committed-binding read, whose CurrentRoomId/RoundGeneration are the participation facts.
/// </summary>
public sealed class RedesignParticipationRecoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20_000);

    private sealed class FakeIdentityStore : IStreamerIdentityStore
    {
        public StreamerSessionState State = new("streamer-fake", "tenant-a", 1, "gateway-1", "connection-1",
            1, Revoked: false, Closed: false, Expired: false, Banned: false, Current: true);
        /// <summary>The committed binding the store answers for; the pointer fields are the
        /// participation facts a lost notice would otherwise leave unknown.</summary>
        public StreamerBindingStatus? BindingStatus;
        public bool FailBindingRead;
        public int Mounts;

        public Task<StreamerSessionState?> GetSessionStateAsync(string tenantId, string sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult<StreamerSessionState?>(State);

        public Task<StreamerConnection> AttachConnectionAsync(string tenantId, string sessionId,
            string gatewayInstanceId, string connectionId, string operationId, byte[] currentSecret,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerConnection(sessionId, "streamer-fake", 1, 2, false));

        public Task<StreamerBindingStatus?> GetActiveBindingAsync(string tenantId, string streamerId,
            CancellationToken cancellationToken = default)
            => FailBindingRead
                ? throw new InvalidOperationException("binding storage down")
                : Task.FromResult(BindingStatus);

        public Task<StreamerBinding> StartGameplayAsync(string tenantId, string liveSessionId, string sessionId,
            string gameId, string operationId, CancellationToken cancellationToken = default)
        {
            Mounts++;
            // A fresh mount is a new binding (new id and generation) that holds no round yet.
            return Task.FromResult(new StreamerBinding($"binding-{Mounts}", "live-fake", gameId, Mounts, "Ready", false));
        }

        public Task<string> ResolveIdentityAsync(string tenantId, string platform, string appId, string openId,
            CancellationToken cancellationToken = default) => Task.FromResult("streamer-fake");

        public Task<StreamerSessionGrant> LoginAsync(string tenantId, string streamerId, string gatewayInstanceId,
            string connectionId, byte[] nextResumeSecret, CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerSessionGrant(streamerId, "session-fake", 1,
                Now.UtcDateTime.AddHours(1), false));

        public Task<StreamerSessionGrant> ResumeAsync(string tenantId, string previousSessionId, string gatewayInstanceId,
            string connectionId, string operationId, byte[] previousSecret, byte[] nextSecret,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerSessionGrant("streamer-fake", "session-fake", 2,
                Now.UtcDateTime.AddHours(1), false));

        public Task<bool> ConnectionClosedAsync(string tenantId, string sessionId, string gatewayInstanceId,
            string connectionId, long connectionGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<StreamerSessionRevocation> RevokeSessionAsync(string tenantId, string sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerSessionRevocation(sessionId, "streamer-fake", 1, true, null, null, 0));

        public Task<string?> GetSessionTenantAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string> OpenLiveSessionAsync(string tenantId, string streamerId, string platform, string appId,
            string platformRoomKey, string canonicalLiveSessionKey, CancellationToken cancellationToken = default)
            => Task.FromResult("live-fake");

        public Task<string?> GetLiveSessionIdAsync(string tenantId, string streamerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("live-fake");
    }

    private sealed class LoadedProfileStore : IStreamerProfileStore
    {
        public Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StreamerProfile?> LoadAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StreamerProfile?>(new StreamerProfile(1, 1, "{}"));
        public Task<StreamerProfile> PatchAsync(string tenantId, string gameId, string streamerId,
            string operationId, long expectedVersion, int schemaVersion, string dataJson,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerProfile(expectedVersion + 1, schemaVersion, dataJson));
    }

    private sealed class LoadedAssetStore : IStreamerAssetStore
    {
        public Task<AssetChangeResult> ChangeAsync(string tenantId, string gameId, string streamerId,
            string operationId, string assetId, long delta, string reason,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AssetChangeResult(delta, 1, Duplicate: false));
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
            new LoadedAssetStore(),
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

    private static AttachConnectionCommand Attach() => new(
        "session-fake", "attach-op-1", RandomNumberGenerator.GetBytes(32), "gateway-2", "connection-2");

    [Fact]
    public async Task AnAttachOnAnAbsentActorRecoversTheCommittedParticipation()
    {
        var rig = New();
        // The committed binding answers with a room pointer: a round started while the entity
        // had no in-memory actor (the notice was retired as an absent-actor no-op).
        rig.Store.BindingStatus = new StreamerBindingStatus("live-fake", "binding-1", "game-a", 1, "Ready",
            "round-1", 3);

        await rig.Coordinator.AttachConnectionAsync("tenant-a", Attach());

        var actor = rig.Registry.Find("tenant-a", "streamer-fake");
        Assert.NotNull(actor);
        Assert.Equal(StreamerActorState.Ready, actor!.State);
        Assert.Equal("session-fake", actor.SessionId);
        // The lost participation is recovered from the committed binding, not from a notice.
        Assert.Equal("round-1", actor.Facts!.ActiveBinding!.CurrentRoomId);
        Assert.Equal(3, actor.Facts.ActiveBinding.RoundGeneration);
    }

    [Fact]
    public async Task AnAttachOnAPresentActorSkipsRederivation()
    {
        var rig = New();
        rig.Store.BindingStatus = new StreamerBindingStatus("live-fake", "binding-1", "game-a", 1, "Ready",
            null, 0);
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        var actor = rig.Registry.Find("tenant-a", "streamer-fake");
        Assert.NotNull(actor);

        // The committed binding now names a round the login-time facts do not: a present actor
        // must not be re-derived on the common gateway-switch path (the notice covers it).
        rig.Store.BindingStatus = new StreamerBindingStatus("live-fake", "binding-1", "game-a", 1, "Ready",
            "round-9", 9);
        await rig.Coordinator.AttachConnectionAsync("tenant-a", Attach());

        Assert.Same(actor, rig.Registry.FindBySession("tenant-a", ready.SessionId));
        Assert.Null(actor!.Facts!.ActiveBinding!.CurrentRoomId);
        Assert.Equal(0, actor.Facts.ActiveBinding.RoundGeneration);
    }

    [Fact]
    public async Task AnAttachOnAnAbsentActorFailsClosedWhenTheStoreCannotRecover()
    {
        var rig = New();
        rig.Store.BindingStatus = new StreamerBindingStatus("live-fake", "binding-1", "game-a", 1, "Ready", null, 0);
        rig.Store.FailBindingRead = true;

        await Assert.ThrowsAsync<EntityRecoveryFailedException>(
            () => rig.Coordinator.AttachConnectionAsync("tenant-a", Attach()));

        var actor = rig.Registry.Find("tenant-a", "streamer-fake");
        Assert.NotNull(actor);
        Assert.Equal(StreamerActorState.RecoveryFailed, actor!.State);
    }

    [Fact]
    public async Task AMountOnAnAbsentActorMaterializesTheEntity()
    {
        var rig = New();
        rig.Store.BindingStatus = new StreamerBindingStatus("live-fake", "binding-1", "game-a", 1, "Ready",
            null, 0);
        var ready = await rig.Coordinator.LoginAsync("tenant-a", Command(rig.IssueCredential));
        var actor = rig.Registry.Find("tenant-a", "streamer-fake");
        Assert.NotNull(actor);
        rig.Registry.Remove(actor!); // simulate a restart: the in-memory entity is gone

        var binding = await rig.Coordinator.StartGameplayAsync("tenant-a", ready.AccessCredential, "game-a", "op-1");

        // The mount materializes a fresh actor instead of proceeding entity-less.
        var remounted = rig.Registry.Find("tenant-a", "streamer-fake");
        Assert.NotNull(remounted);
        Assert.Equal(binding.BindingId, remounted!.Facts!.ActiveBinding!.BindingId);
        // A freshly mounted binding holds no round: the re-derivation is for the entity, not a pointer.
        Assert.Null(remounted.Facts.ActiveBinding.CurrentRoomId);
        Assert.Equal(0, remounted.Facts.ActiveBinding.RoundGeneration);
    }
}
