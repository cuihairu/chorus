using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-14: destroying a released round's runtime entity on the game host. The epoch
/// ceiling is the released ownership row's own bump — an actor strictly below it belongs to
/// the settled round and goes (its frozen result stays queryable, FIX-ROOM-05), an actor at
/// or above it is a newer generation and is never touched, and a live leftover of the
/// settled round is disposed because its authority ended at the release. The destroy is
/// idempotent and never throws: unknown and already-destroyed rooms answer clean.
/// </summary>
public sealed class RedesignDestroyRoomUnitTests
{
    [Fact]
    public async Task DestroyRoomRemovesSettledActorsBelowTheCeilingAndKeepsTheResultTail()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-des-1", epoch: 1, durationTicks: 3))).Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(
            runtime.FindRoom("room-des-1")?.Completed == true)),
            "The room never finished.");

        // The pre-release row's epoch equals the running epoch: nothing is strictly below
        // the ceiling, so the normal destroy cannot remove the entity before the finalize.
        var early = await runtime.DestroyRoomAsync("room-des-1", releaseEpoch: 1);
        Assert.False(early.Destroyed);
        Assert.True(early.SparedNewGeneration);
        Assert.NotNull(runtime.FindRoom("room-des-1"));

        // The release bumps the row to the running epoch + 1: the settled actor is now
        // strictly below the ceiling and goes, its frozen result staying behind.
        var destroyed = await runtime.DestroyRoomAsync("room-des-1", releaseEpoch: 2);
        Assert.True(destroyed.Destroyed);
        Assert.False(destroyed.SparedNewGeneration);
        Assert.Null(runtime.FindRoom("room-des-1"));
        var final = runtime.FindResult("room-des-1");
        Assert.NotNull(final);
        Assert.Equal(1, final!.Epoch);
        Assert.Contains("ranking", final.ResultJson);

        // Repeated destroy and an unknown room both answer clean — never a throw.
        Assert.Equal((false, false), await runtime.DestroyRoomAsync("room-des-1", releaseEpoch: 2));
        Assert.Equal((false, false), await runtime.DestroyRoomAsync("room-des-never", releaseEpoch: 2));
    }

    [Fact]
    public async Task DestroyRoomSparesNewerGenerationsAndDisposesLiveLeftovers()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        // A live leftover of the settled round: its round is released on the data plane,
        // so a still-ticking actor below the ceiling is unauthorized residue — the dispose
        // is local teardown and the room stays identified in the host's reports.
        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-des-old", epoch: 1, durationTicks: 200))).Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The leftover room never committed a frame.");
        var destroyed = await runtime.DestroyRoomAsync("room-des-old", releaseEpoch: 2);
        Assert.True(destroyed.Destroyed);
        Assert.Null(runtime.FindRoom("room-des-old"));
        Assert.Null(runtime.FindResult("room-des-old")); // nothing was committed to cache
        Assert.Contains(runtime.RoomStatuses(), status => status.RoomId == "room-des-old");

        // A newer generation (a takeover raised the room past the released epoch): the
        // destroy of the old round must leave it alive and ticking.
        Assert.False((await runtime.PrepareRoomAsync(Prepare("room-des-new", epoch: 3, durationTicks: 200))).Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 2)),
            "The newer-generation room never committed a frame.");
        var spared = await runtime.DestroyRoomAsync("room-des-new", releaseEpoch: 2);
        Assert.False(spared.Destroyed);
        Assert.True(spared.SparedNewGeneration);
        Assert.NotNull(runtime.FindRoom("room-des-new"));
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 3)),
            "The spared newer generation stopped ticking.");
    }

    private static PrepareRoomRequest Prepare(string roomId, long epoch, long durationTicks) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch,
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
