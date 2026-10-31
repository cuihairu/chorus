using System.Text.Json;
using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-04, consumption half: a directly pushed event that took an in-memory seat is
/// consumed by the tick's frame commit — the commit references the event id, the frame's
/// transaction consumes the delivery, the subscriber sees the input in the delta and the
/// effect in the result, and the whole room runs without one inbox pull. A duplicate push
/// keeps its single seat, so its effect lands exactly once. The recoverable inbox cursor
/// stays the consumption authority: what the commit consumes is the pending delivery the
/// durable leg wrote, wherever the actor learned its id.
/// </summary>
public sealed class RedesignSeatConsumptionUnitTests
{
    [Fact]
    public async Task ASeatedPushIsConsumedIntoTheFrameWithoutAnyInboxPull()
    {
        var store = new ScriptedFrameStore();
        var actor = NewRoom(store, durationTicks: 1);
        var subscriber = actor.Subscribe(0, 1);

        // The edge's handoff after the durable leg committed, then the wake.
        Assert.True(actor.TryHand("seat-gift-1", "binding-seat",
            """{"action":"boost","level":3}""", "simulator"));
        actor.Wake();
        actor.Start();

        // The one-tick room consumes the seat on its only tick and commits the final frame.
        var delta = await NextAsync(subscriber);
        Assert.NotNull(delta);
        Assert.Equal(SyncMessage.KindOneofCase.Delta, delta.KindCase);
        Assert.Contains("seat-gift-1", delta.Delta.InputEventIds);
        var result = await NextAsync(subscriber);
        Assert.NotNull(result);
        Assert.Equal(SyncMessage.KindOneofCase.RoundResult, result.KindCase);
        using (var document = JsonDocument.Parse(result.RoundResult.ResultJson))
        {
            Assert.Equal(36L, ScoreOf(document, "red")); // the flat 12 × level 3 of 1.1.0
        }

        // The push path needed no inbox read: the room's whole life consumed its input
        // from the seat, and the commit carries exactly that event id.
        Assert.Equal(0, store.InboxReadCount);
        var commits = store.CommitsSnapshot();
        Assert.Equal(1, commits.Length);
        Assert.Equal(["seat-gift-1"], commits[0].InputEventIds);
        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    [Fact]
    public async Task ADuplicatePushKeepsItsSingleEffect()
    {
        var store = new ScriptedFrameStore();
        var actor = NewRoom(store, durationTicks: 2);
        var subscriber = actor.Subscribe(0, 1);

        // The same event id handed twice — the duplicate push's idempotent re-hand keeps
        // the single seat, so the ticks reference and apply it exactly once.
        Assert.True(actor.TryHand("seat-gift-2", "binding-seat",
            """{"action":"boost","level":3}""", "simulator"));
        Assert.True(actor.TryHand("seat-gift-2", "binding-seat",
            """{"action":"boost","level":3}""", "simulator"));
        actor.Wake();
        actor.Start();

        var result = await ResultAsync(subscriber);
        Assert.NotNull(result);
        using (var document = JsonDocument.Parse(result.RoundResult.ResultJson))
        {
            Assert.Equal(36L, ScoreOf(document, "red")); // one boost, one effect
        }
        // The seat carried the first tick; the drained second tick fell back to the cursor
        // pull (an empty read — the delivery is already Consumed, so it replays nothing).
        Assert.Equal(1, store.InboxReadCount);
        var references = store.CommitsSnapshot()
            .SelectMany(commit => commit.InputEventIds).Count(id => id == "seat-gift-2");
        Assert.Equal(1, references);
        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    private static long ScoreOf(JsonDocument result, string faction) =>
        result.RootElement.GetProperty("ranking").EnumerateArray()
            .Single(entry => entry.GetProperty("faction").GetString() == faction)
            .GetProperty("score").GetInt64();

    private static async Task<SyncMessage?> ResultAsync(RoomSubscriber subscriber)
    {
        while (true)
        {
            var message = await NextAsync(subscriber);
            if (message is null || message.KindCase == SyncMessage.KindOneofCase.RoundResult)
                return message;
        }
    }

    private static async Task<SyncMessage?> NextAsync(RoomSubscriber subscriber)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { return await subscriber.NextAsync(timeout.Token); }
        catch (OperationCanceledException) { return null; }
    }

    private static RoomActor NewRoom(ScriptedFrameStore store, long durationTicks)
    {
        var module = new ExampleGameModuleV2();
        var participants = new List<GameParticipant>
        {
            new("host-seat", GameStartConfig.RedFaction), new("guest-seat", GameStartConfig.BlueFaction)
        };
        var initial = module.CreateInitialState(new GameStartConfig(durationTicks, 777, participants));
        var bindings = new Dictionary<string, string> { ["binding-seat"] = "host-seat" };
        return new RoomActor(new ScriptedLifecycle(), store, module, "game-seat", "room-seat",
            "game-a", 1, "cfg-seat", initial, bindings);
    }

    /// <summary>The data plane as the actor sees it: every commit answers a receipt (the
    /// store's idempotency is not under test here), and the inbox read is counted — the
    /// seat path must never trigger one.</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private int _inboxReads;
        private readonly List<CommitFrameRequest> _commits = new();

        public int InboxReadCount { get { lock (_gate) return _inboxReads; } }
        public CommitFrameRequest[] CommitsSnapshot() { lock (_gate) return _commits.ToArray(); }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _inboxReads++;
            var reply = new ReadInboxReply();
            return new AsyncUnaryCall<ReadInboxReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commits.Add(request);
            var receipt = new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId, Tick = request.PrevTick + 1,
                StateVersion = request.PrevStateVersion + 1
            };
            return new AsyncUnaryCall<FrameReceiptReply>(Task.FromResult(receipt),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    private sealed class ScriptedLifecycle : IRoomLifecycle
    {
        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
