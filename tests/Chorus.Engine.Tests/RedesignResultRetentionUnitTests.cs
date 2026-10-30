using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Bounded retention of finished results (FIX-ROOM-05): releasing a finished room keeps its
/// frozen result queryable while the actor itself is gone, the cache holds only the newest
/// limited number of releases, and a late subscriber on the result's epoch receives the
/// frozen result — a stale epoch is told where to resubscribe.
/// </summary>
public sealed class RedesignResultRetentionUnitTests
{
    [Fact]
    public async Task AReleasedRoomStaysQueryableWithoutItsActor()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-keep", durationTicks: 3))).Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(
            runtime.FindRoom("room-keep")?.Completed == true)),
            "The room never finished.");

        Assert.True(await runtime.ReleaseRoomAsync("room-keep"));
        Assert.Null(runtime.FindRoom("room-keep")); // the actor is gone…
        var final = runtime.FindResult("room-keep"); // …the outcome is not
        Assert.NotNull(final);
        Assert.Equal(1, final!.Epoch);
        Assert.Equal(3, final.Tick);
        Assert.Contains("ranking", final.ResultJson);
        Assert.False(await runtime.ReleaseRoomAsync("room-keep")); // a released room releases again: no
        Assert.False(await runtime.ReleaseRoomAsync("room-never")); // an unknown room: also no
    }

    [Fact]
    public async Task ARunningRoomIsNeverReleaseMaterial()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-live"))).Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The room never committed a frame.");
        Assert.False(await runtime.ReleaseRoomAsync("room-live"));
        Assert.NotNull(runtime.FindRoom("room-live")); // untouched, still ticking
        Assert.Null(runtime.FindResult("room-live"));
    }

    [Fact]
    public async Task TheRetentionCacheKeepsOnlyTheNewestReleases()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement())
        {
            RetainedResults = 2
        };

        foreach (var roomId in new[] { "room-a", "room-b", "room-c" })
        {
            Assert.False((await runtime.PrepareRoomAsync(Prepare(roomId, durationTicks: 3))).Duplicate);
            Assert.True(await WaitForAsync(() => Task.FromResult(
                runtime.FindRoom(roomId)?.Completed == true)), $"{roomId} never finished.");
            Assert.True(await runtime.ReleaseRoomAsync(roomId));
        }

        Assert.Null(runtime.FindResult("room-a")); // fell off the bounded end
        Assert.NotNull(runtime.FindResult("room-b"));
        Assert.NotNull(runtime.FindResult("room-c"));
        Assert.All(new[] { "room-a", "room-b", "room-c" },
            roomId => Assert.Null(runtime.FindRoom(roomId))); // no release kept a whole actor
    }

    [Fact]
    public void ALateSubscriberGetsTheFrozenResultOrTheEpochToResubscribeAt()
    {
        var final = new FinalRoundResult(Epoch: 2, Tick: 40, StateVersion: 41,
            ResultJson: """{"ranking":[{"faction":"red","score":9}]}""");

        var served = RoomRuntime.LateSubscriberAnswer("room-rel", requestedEpoch: 2, final);
        Assert.Equal(SyncMessage.KindOneofCase.RoundResult, served.KindCase);
        Assert.Equal("room-rel", served.RoundResult.RoomId);
        Assert.Equal(2, served.RoundResult.Epoch);
        Assert.Equal(40, served.RoundResult.Tick);
        Assert.Equal(41, served.RoundResult.StateVersion);
        Assert.Contains("ranking", served.RoundResult.ResultJson);

        var stale = RoomRuntime.LateSubscriberAnswer("room-rel", requestedEpoch: 1, final);
        Assert.Equal(SyncMessage.KindOneofCase.ResyncRequired, stale.KindCase);
        Assert.Equal(2, stale.ResyncRequired.CurrentEpoch);
        Assert.Contains("epoch 2", stale.ResyncRequired.Reason);
    }

    private static PrepareRoomRequest Prepare(string roomId, long durationTicks = 200) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = $"op-{roomId}", DurationTicks = durationTicks,
        RandomSeed = 777, TickMs = 25,
        Participants =
        {
            new GameParticipantProto { ParticipantId = "host-k", BindingId = "binding-host-k", Faction = GameStartConfig.RedFaction },
            new GameParticipantProto { ParticipantId = "guest-k", BindingId = "binding-guest-k", Faction = GameStartConfig.BlueFaction }
        }
    };

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

    /// <summary>The round store: every admission succeeds.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Preparing" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoundAdmissionReply> SuspendRoundAsync(SuspendRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Suspended" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    /// <summary>The frame store: plain inbox and commit successes.</summary>
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
            return new(Task.FromResult(new FrameReceiptReply
                {
                    CommitId = request.CommitId, RoomId = request.RoomId,
                    Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
                }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>Settlement always succeeds.</summary>
    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
