using System.Diagnostics;
using System.Security.Cryptography;
using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-ROOM-07, the unit leg: one RoomRuntime cycles forty rounds through prepare,
/// completion, settlement and authorized release. What must not grow with history is the
/// live machinery: after release every room's actor is gone from the dictionary, the
/// frozen results respect their bounded cache (oldest evicted first), a room that has not
/// committed its result is never release material, and a live subscription ends with the
/// frozen result instead of leaking. Memory trend is recorded, not asserted: the bound
/// this test enforces is structural.
/// </summary>
public sealed class RedesignRoomTurnoverSoakUnitTests
{
    private readonly ITestOutputHelper _output;

    public RedesignRoomTurnoverSoakUnitTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RoundsCycleThroughReleaseWithoutGrowingLiveActorsOrTheResultCache()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        var settlement = new ScriptedSettlement();
        await using var runtime = new RoomRuntime(rounds, frames, settlement)
        {
            // A small cache makes the bound bite inside the run: forty released results,
            // eight retained.
            RetainedResults = 8
        };

        const int total = 40;
        var subscribedRooms = new HashSet<string> { "room-soak-5", "room-soak-37" };
        var subscribers = new Dictionary<string, RoomSubscriber>();
        var rssTrend = new List<(int Round, long Rss)>();
        for (var index = 0; index < total; index++)
        {
            var roomId = $"room-soak-{index}";
            var prepared = await runtime.PrepareRoomAsync(Prepare(roomId, index));
            Assert.False(prepared.Duplicate, $"Round {index} prepared as a duplicate.");
            if (subscribedRooms.Contains(roomId))
                subscribers[roomId] = runtime.FindRoom(roomId)!.Subscribe(0, 0);

            // The room runs its whole life inside this iteration: ticks, final commit,
            // settlement — then the authorized release takes the actor away.
            var actor = runtime.FindRoom(roomId);
            Assert.NotNull(actor);
            var settled = await WaitForAsync(() => Task.FromResult(
                actor!.FinalResult() is not null && settlement.CommitCalls == index + 1));
            Assert.True(settled, $"Round {index} never committed its result and settled.");
            Assert.True(await runtime.ReleaseRoomAsync(roomId), $"Round {index} was not release material.");
            Assert.Null(runtime.FindRoom(roomId));
            Assert.False(await runtime.ReleaseRoomAsync(roomId), "A repeated release answered true.");

            if (index % 8 == 7)
            {
                rssTrend.Add((index + 1, Process.GetCurrentProcess().WorkingSet64));
                _output.WriteLine(
                    $"rounds={index + 1} rss={rssTrend[^1].Rss / (1024 * 1024)}MB " +
                    $"allocated={GC.GetTotalAllocatedBytes() / (1024 * 1024)}MB");
            }
        }

        // Nothing live survives the history: every actor left the dictionary (an actor
        // count that grew with rounds would appear here as a live entry).
        for (var index = 0; index < total; index++)
            Assert.Null(runtime.FindRoom($"room-soak-{index}"));

        // The bounded result cache kept exactly the newest eight rounds; the oldest
        // releases were evicted, not accumulated.
        for (var index = 0; index < total - 8; index++)
            Assert.Null(runtime.FindResult($"room-soak-{index}"));
        for (var index = total - 8; index < total; index++)
        {
            var result = runtime.FindResult($"room-soak-{index}");
            Assert.NotNull(result);
            Assert.True(result!.Tick > 0);
        }
        Assert.False(await runtime.ReleaseRoomAsync("room-soak-unknown"), "An unknown room released.");

        // The subscriptions ended with the frozen result and drained — none outlived its
        // room (FIX-ROOM-05's late-subscriber contract, observed from the pump side).
        foreach (var (roomId, subscriber) in subscribers)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            SyncMessage? message;
            SyncMessage? final = null;
            while ((message = await subscriber.NextAsync(cancellation.Token)) is not null)
                if (message.KindCase == SyncMessage.KindOneofCase.RoundResult)
                    final = message;
            Assert.NotNull(final);
            Assert.Equal(roomId, final!.RoundResult.RoomId);
        }

        // The runtime still admits fresh work after the whole history: capacity did not
        // leak into the dictionary, the cache or the gate.
        var fresh = await runtime.PrepareRoomAsync(Prepare("room-soak-fresh", total));
        Assert.False(fresh.Duplicate);
        Assert.NotNull(runtime.FindRoom("room-soak-fresh"));
        _output.WriteLine($"trend rss MB: {string.Join(", ", rssTrend.Select(sample => sample.Rss / (1024 * 1024)))}");
    }

    [Fact]
    public async Task ARoomThatHasNotCommittedItsResultIsNeverReleaseMaterial()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        var prepared = await runtime.PrepareRoomAsync(Prepare("room-soak-live", 0));
        Assert.False(prepared.Duplicate);
        var actor = runtime.FindRoom("room-soak-live");
        Assert.NotNull(actor);
        // Mid-life the release hook refuses: the room still ticks, and no caller may
        // mistake a live room for recycled capacity.
        Assert.False(await runtime.ReleaseRoomAsync("room-soak-live"));
        Assert.NotNull(runtime.FindRoom("room-soak-live"));

        var settled = await WaitForAsync(() =>
            Task.FromResult(actor!.FinalResult() is not null));
        Assert.True(settled, "The room never finished its short life.");
        Assert.True(await runtime.ReleaseRoomAsync("room-soak-live"));
        Assert.Null(runtime.FindRoom("room-soak-live"));
    }

    private static PrepareRoomRequest Prepare(string roomId, int seed) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = "prepare-" + roomId, DurationTicks = 40, RandomSeed = 777 + seed, TickMs = 1,
        Participants =
        {
            new GameParticipantProto { ParticipantId = "host-" + roomId, BindingId = "binding-" + roomId + "-host", Faction = GameStartConfig.RedFaction },
            new GameParticipantProto { ParticipantId = "guest-" + roomId, BindingId = "binding-" + roomId + "-guest", Faction = GameStartConfig.BlueFaction }
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

    /// <summary>The round store with counted admissions and confirmations.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private int _prepareCalls;

        public int PrepareCalls { get { lock (_gate) return _prepareCalls; } }

        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _prepareCalls++;
            return new AsyncUnaryCall<RoundAdmissionReply>(
                Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Preparing" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<RoomTakeoverReply> ConfirmRoomOwnershipAsync(
            ConfirmRoomOwnershipRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoomTakeoverReply
            {
                OwnerInstanceId = request.OwnerInstance, Status = "Active"
            }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    /// <summary>The frame store: plain successes for every commit and snapshot.</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId,
                Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
            }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<SnapshotReceiptReply> PutSnapshotAsync(PutSnapshotRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new SnapshotReceiptReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    /// <summary>Settlement always succeeds; each finished room settles exactly once.</summary>
    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        private readonly object _gate = new();
        private int _commitCalls;

        public int CommitCalls { get { lock (_gate) return _commitCalls; } }

        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commitCalls++;
            return new AsyncUnaryCall<CommitResultReply>(Task.FromResult(new CommitResultReply()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }
}
