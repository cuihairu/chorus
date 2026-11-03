using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-06 acceptance (持久暂停与时间冻结): a PauseAtBoundary room parks itself at a
/// committed boundary once every control session has been offline past the declared grace
/// (authoritative-sync.md:42) — the pause is a durable store fact, not a stopped local
/// timer, so the frozen head is what a restart restores. A ContinueToEnd room never pauses
/// and runs to its result; a reconnect inside the grace resets the window so the grace
/// never carries across an interruption. The store-persistence and recovery round trip is
/// in <see cref="RedesignSync06PauseHostTests"/>.
/// </summary>
public sealed class RedesignSync06PauseUnitTests
{
    [Fact]
    public async Task APauseAtBoundaryRoomParksItselfOnceTheGraceElapses()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        // 1s grace: the room pauses about a second after the last control session drops.
        // Both sessions are online before the loop starts, so the all-offline window opens
        // exactly at the disconnect — never at the seed instant.
        var actor = NewRoom(store, lifecycle, PauseAtBoundary(graceSeconds: 1), durationTicks: 500);
        ConnectBoth(actor, 1);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(store.CommitCount >= 2)),
            "The room never committed a frame.");
        DisconnectBoth(actor, 1);

        Assert.True(await WaitForAsync(() => Task.FromResult(lifecycle.PauseCount == 1)),
            "The room never parked itself after the grace elapsed.");
        Assert.True(await WaitForAsync(() => Task.FromResult(actor.Status().State == RoomRunState.Paused)),
            "The actor never entered the Paused state.");
        // The freeze is at the committed boundary the pause names — the actor's real head.
        Assert.Equal(actor.Status().LastCommittedTick, lifecycle.PauseTick);
        Assert.True(lifecycle.PauseTick >= 1, "The pause parked before any frame committed.");
        Assert.True(lifecycle.PauseVersion >= 1, "The pause carried no state version.");
        Assert.Contains("all control sessions offline", lifecycle.PauseReason, StringComparison.Ordinal);
        Assert.Empty(lifecycle.SuspendReasons);

        // No frame commits after the pause: the head is frozen, not merely quiet.
        var head = store.CommitCount;
        await Task.Delay(150);
        Assert.Equal(head, store.CommitCount);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task AContinueToEndRoomNeverPausesAndRunsToItsResult()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        // Both control sessions are offline from the seed — the strongest all-offline case —
        // and the grace is short, so a room that (wrongly) paused would park well before the
        // result. ContinueToEnd keeps computing and reaches the result instead.
        var actor = NewRoom(store, lifecycle, ContinueToEnd(graceSeconds: 1),
            durationTicks: 150, tickMs: 10);
        actor.Start();

        Assert.True(await WaitForAsync(() => Task.FromResult(actor.Completed)),
            "The ContinueToEnd room never reached its result.");
        Assert.Equal(0, lifecycle.PauseCount);
        Assert.Equal(new[] { "room-s06" }, lifecycle.SettledRooms);
        Assert.True(store.CommitCount >= 150, $"Expected ~150 commits, saw {store.CommitCount}.");
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task AReconnectWithinTheGraceResetsTheWindowAndDelaysThePause()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        // 3s grace so the within-grace reconnection is unambiguous even under load.
        var actor = NewRoom(store, lifecycle, PauseAtBoundary(graceSeconds: 3), durationTicks: 1000);
        ConnectBoth(actor, 1);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(store.CommitCount >= 2)),
            "The room never committed a frame.");
        DisconnectBoth(actor, 1);

        // Reconnect one control session inside the grace: no longer all-offline, so the
        // room keeps ticking instead of parking.
        await Task.Delay(500);
        actor.ReportControlConnected(2, "host-s06", PresenceLinkKind.Control);
        await Task.Delay(1000); // ~1s after the reconnect, still inside the 3s grace
        Assert.Equal(0, lifecycle.PauseCount);
        Assert.Equal(RoomRunState.Running, actor.Status().State);

        // Drop again: the all-offline run restarts from this instant, so the grace does not
        // carry the earlier run — the pause lands only after the fresh 3s window elapses.
        actor.ReportControlDisconnected(2, "host-s06", ControlLinkReason.Disconnected);
        Assert.True(await WaitForAsync(() => Task.FromResult(lifecycle.PauseCount == 1)),
            "The room never parked after the restarted grace elapsed.");
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task AControlSessionStillOnlineKeepsTheRoomMixedNeverPausing()
    {
        var store = new ScriptedFrameStore();
        var lifecycle = new ScriptedLifecycle();
        var actor = NewRoom(store, lifecycle, PauseAtBoundary(graceSeconds: 1), durationTicks: 500);
        ConnectBoth(actor, 1);
        actor.Start();
        Assert.True(await WaitForAsync(() => Task.FromResult(store.CommitCount >= 2)),
            "The room never committed a frame.");
        // One control session stays live; the other drops. A single live control session
        // keeps the round Mixed — never a pause (FIX-SYNC-05).
        actor.ReportControlDisconnected(1, "guest-s06", ControlLinkReason.Disconnected);

        await Task.Delay(1200); // well past the 1s grace
        Assert.Equal(0, lifecycle.PauseCount);
        Assert.Equal(RoomRunState.Running, actor.Status().State);
        await actor.DisposeAsync();
    }

    [Fact]
    public async Task ARecoveredPausedRoomAnswersPausedWithNoActorLoop()
    {
        // The recovery entry for a paused actor this host still holds answers Paused with
        // the frozen head, never a tick loop (the running/replaying paths are untouched).
        var status = new RoomActorStatus(RoomRunState.Paused, 1, 42, null);
        var reply = RoomRuntime.AnswerForKnownActor(Recover(epoch: 1), status);
        Assert.True(reply.Recovered);
        Assert.Equal("Paused", reply.Status);
        Assert.Equal(42, reply.LastTick);
        Assert.Contains("paused", reply.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecoveryOfAPausedPlanRestoresPausedWithNoTickLoop()
    {
        // The plan-path recovery (the room no longer held locally): a plan that loads as
        // Paused is confirmed under the fenced ownership and answered Paused — the recovery
        // starts no actor, so nothing ticks past the frozen boundary (FIX-SYNC-06).
        var rounds = new ScriptedRoundStore();
        await using var runtime = new RoomRuntime(rounds, new PausedPlanFrameStore(), new ScriptedSettlement());

        var reply = await runtime.RecoverRoomAsync(new RecoverRoomRequest
        {
            RoomId = "room-s06", GameId = "game-a", OwnerInstanceId = "game-1",
            Epoch = 1, TickMs = 25
        });

        Assert.True(reply.Recovered);
        Assert.Equal("Paused", reply.Status);
        Assert.Equal(17, reply.LastTick);
        Assert.Contains("stays paused", reply.Note, StringComparison.Ordinal);
        Assert.True(rounds.ConfirmCalls == 1, "The paused recovery never confirmed ownership.");
        // No actor was started: the room is frozen, not running.
        Assert.Null(runtime.FindRoom("room-s06"));
    }

    private static ModuleOfflinePolicy PauseAtBoundary(int graceSeconds) => new(
        AllOfflinePolicy.PauseAtBoundary, graceSeconds, 120, 1, PausedInputPolicy.Buffer);

    private static ModuleOfflinePolicy ContinueToEnd(int graceSeconds) => new(
        AllOfflinePolicy.ContinueToEnd, graceSeconds, 120, 1, PausedInputPolicy.Buffer);

    private static void ConnectBoth(RoomActor actor, long generation)
    {
        actor.ReportControlConnected(generation, "host-s06", PresenceLinkKind.Control);
        actor.ReportControlConnected(generation, "guest-s06", PresenceLinkKind.Control);
    }

    private static void DisconnectBoth(RoomActor actor, long generation)
    {
        actor.ReportControlDisconnected(generation, "host-s06", ControlLinkReason.Disconnected);
        actor.ReportControlDisconnected(generation, "guest-s06", ControlLinkReason.Disconnected);
    }

    private static RecoverRoomRequest Recover(long epoch) => new()
    {
        RoomId = "room-s06", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch, TickMs = 25
    };

    private static RoomActor NewRoom(ScriptedFrameStore store, ScriptedLifecycle lifecycle,
        ModuleOfflinePolicy offline, long durationTicks, int tickMs = 25)
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "sync06-tests", "1.0.0-test", tickMs, 100,
            RedesignGameModuleManifest.DeterministicReplay, offline));
        var participants = new List<GameParticipant>
        {
            new("host-s06", GameStartConfig.RedFaction), new("guest-s06", GameStartConfig.BlueFaction)
        };
        var initial = module.CreateInitialState(
            new GameStartConfig(durationTicks, 777, participants, 0));
        var bindings = new Dictionary<string, string>
        {
            ["binding-host-s06"] = "host-s06", ["binding-guest-s06"] = "guest-s06"
        };
        return new RoomActor(lifecycle, store, module, "game-a", "room-s06", "game-1", 1, "cfg-s06",
            initial, bindings);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(20);
        }
        return await probe();
    }

    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private int _commits;

        public int CommitCount { get { lock (_gate) return _commits; } }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commits++;
            return new AsyncUnaryCall<FrameReceiptReply>(Task.FromResult(new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId,
                Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The runtime asks recorded; the pause facts carry the frozen boundary.</summary>
    private sealed class ScriptedLifecycle : IRoomLifecycle
    {
        private readonly object _gate = new();
        private readonly List<string> _suspends = new();
        private readonly List<string> _settles = new();
        private readonly List<(long Tick, long Version, string Reason)> _pauses = new();

        public int PauseCount { get { lock (_gate) return _pauses.Count; } }
        public long PauseTick { get { lock (_gate) return _pauses[0].Tick; } }
        public long PauseVersion { get { lock (_gate) return _pauses[0].Version; } }
        public string PauseReason { get { lock (_gate) return _pauses[0].Reason; } }
        public string[] SuspendReasons { get { lock (_gate) return _suspends.ToArray(); } }
        public string[] SettledRooms { get { lock (_gate) return _settles.ToArray(); } }

        public Task PauseRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            long tick, long resultVersion, string reason, CancellationToken cancellationToken = default)
        {
            lock (_gate) _pauses.Add((tick, resultVersion, reason));
            return Task.CompletedTask;
        }

        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default)
        {
            lock (_gate) _suspends.Add(reason);
            return Task.CompletedTask;
        }

        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _settles.Add(roomId);
            return Task.CompletedTask;
        }

        public Task ReportEndAsync(string gameId, string roomId, long finalTick, long resultVersion,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>The fenced activation answers Active for the named owner and counts the
    /// confirm — a paused recovery must take the same ownership fence a running one does.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private int _confirms;

        public int ConfirmCalls { get { lock (this) return _confirms; } }

        public override AsyncUnaryCall<RoomTakeoverReply> ConfirmRoomOwnershipAsync(
            ConfirmRoomOwnershipRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (this) _confirms++;
            return new(Task.FromResult(new RoomTakeoverReply
            {
                OwnerInstanceId = request.OwnerInstance, Status = "Active"
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The frame store answering a plan that loads as Paused at the frozen head —
    /// the same identity facts a running plan carries, so the pause never suspends the
    /// room's module facts. The paused recovery consumes only the identity and the head,
    /// never a snapshot or a participant map (it starts no replay); the commit and inbox
    /// calls answer plainly so a hypothetical tick loop would fail loudly on FindRoom.</summary>
    private sealed class PausedPlanFrameStore : FrameStore.FrameStoreClient
    {
        public override AsyncUnaryCall<LoadRecoveryReply> LoadRecoveryAsync(LoadRecoveryRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new LoadRecoveryReply
            {
                Found = true, RoomId = "room-s06", Status = "Paused", Epoch = 1,
                LastTick = 17, StateVersion = 18, TickMs = 25,
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
                RecoveryMode = "DeterministicReplay",
                OfflinePolicy = ModuleOfflinePolicy.ProductionDefault.Canonical
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId,
                Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
            }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    /// <summary>Settlement always succeeds; a paused room never reaches settlement.</summary>
    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
