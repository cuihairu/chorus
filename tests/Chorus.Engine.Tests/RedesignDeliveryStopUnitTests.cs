using Chorus.PlatformGateway;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;
using static Chorus.Rpc.RoundInput;
using static Chorus.Rpc.RoundEnd;

namespace Chorus.Engine.Tests;

/// <summary>
/// The step-4 machinery in unit pins (FIX-END-07, architecture 6.4): the closure record's
/// generation comparison mirrors the receiver's own fence (at or below the closed
/// generation refuses), the drain queue carries first arrivals and late-ingest requests
/// but never duplicates, the cutoff watermark is monotonic, and the drain pass counts only
/// its own room's dispositions before acking the closure's exact facts.
/// </summary>
public sealed class RedesignDeliveryStopUnitTests
{
    [Fact]
    public void TryCloseRecordsTheGenerationAndRefusesOnlyAtOrBelowIt()
    {
        var record = new StopDeliveryRecord();
        Assert.True(record.TryClose("tenant-a", "game-a", "room-1", "end-op-1", 5, 99,
            out var duplicate, out var recorded));
        Assert.False(duplicate);
        Assert.Equal(99, recorded);

        // A newer generation is not this closure's business; the closed generation and the
        // stale snapshots below it refuse — exactly how the receiver's fence compares
        // (FIX-END-06), so the two gates can never disagree on the same push.
        Assert.False(record.IsClosed("room-1", 6));
        Assert.True(record.IsClosed("room-1", 5));
        Assert.True(record.IsClosed("room-1", 4));
        Assert.False(record.IsClosed("room-2", 5));

        // A replay at the recorded version confirms; a different version for the same room
        // is refused with the recorded version named — a late command never closes a newer
        // round through this record.
        Assert.True(record.TryClose("tenant-a", "game-a", "room-1", "end-op-1", 5, 99,
            out var replay, out _));
        Assert.True(replay);
        Assert.False(record.TryClose("tenant-a", "game-a", "room-1", "end-op-2", 6, 100,
            out _, out var refusedAt));
        Assert.Equal(99, refusedAt);
    }

    [Fact]
    public void TheClosureQueuesOneDrainAndTheWatermarkIsMonotonic()
    {
        var record = new StopDeliveryRecord();
        record.TryClose("tenant-a", "game-a", "room-1", "end-op-1", 5, 99, out _, out _);
        // The first arrival owns the drain — the queue holds exactly that one arrival.
        Assert.True(record.DrainQueue.TryRead(out var first));
        Assert.Equal("room-1", first);
        Assert.False(record.DrainQueue.TryRead(out _));

        // The watermark only moves up: a late reply for a lower sequence loses. It records
        // without a closure too — a room's pushes land before the command that closes it,
        // and the closure inherits the observation at install.
        record.RecordCutoff("room-1", 12);
        record.RecordCutoff("room-1", 3);
        record.RecordCutoff("room-2", 40);
        Assert.Equal(12, record.CutoffOf("room-1"));
        Assert.Equal(40, record.CutoffOf("room-2"));
        Assert.Equal(0, record.CutoffOf("room-none"));

        // A late ingest into the closed room asks for one more pass; a room without a
        // closure asks for nothing — there is nothing to dispose as closed.
        record.ObserveApp("simulator", "app-1");
        Assert.Equal(("simulator", "app-1"), Assert.Single(record.ObservedApps()));
        record.RequestDrain("room-1");
        Assert.True(record.DrainQueue.TryRead(out var again));
        Assert.Equal("room-1", again);
        record.RequestDrain("room-2");
        Assert.False(record.DrainQueue.TryRead(out _));
    }

    [Fact]
    public async Task TheDrainPassCountsOnlyItsRoomAndAcksTheClosureFacts()
    {
        var record = new StopDeliveryRecord();
        record.TryClose("tenant-a", "game-a", "room-1", "end-op-1", 5, 99, out _, out _);
        record.ObserveApp("simulator", "app-1");
        record.RecordCutoff("room-1", 12);
        var input = new ScriptedRoundInput(new RouteEventsReply
        {
            Events =
            {
                new RoutedEventProto { EventId = "e-ours", RoomId = "room-1", Disposition = "OutOfRound" },
                new RoutedEventProto { EventId = "e-theirs", RoomId = "room-2", Disposition = "Routed" }
            }
        });
        var roundEnd = new ScriptedRoundEnd();
        var drain = new DeliveryStopDrainService(record, null!, null!);

        Assert.True(await drain.DrainAndAckAsync("room-1", input, roundEnd, CancellationToken.None));

        // The pass routed the observed pair under a fresh operation id (a repeated id would
        // replay the earlier pass's recorded reply and count its events twice).
        var routed = Assert.Single(input.Seen);
        Assert.Equal("simulator", routed.Platform);
        Assert.Equal("app-1", routed.AppId);
        Assert.StartsWith("drain:room-1:", routed.OperationId, StringComparison.Ordinal);

        // The ack carries the closure's exact facts and only this room's counts — another
        // room's Routed item is a side effect of the shared (platform, app) scope.
        var ack = Assert.Single(roundEnd.Acks);
        Assert.Equal("tenant-a", ack.TenantId);
        Assert.Equal("game-a", ack.GameId);
        Assert.Equal("room-1", ack.RoomId);
        Assert.Equal("end-op-1", ack.EndOperationId);
        Assert.Equal(99, ack.CloseRouteVersion);
        Assert.Equal(12, ack.CutoffSequence);
        Assert.Equal(1, ack.DrainedOutOfRound);
        Assert.Equal(0, ack.DrainedRouted);
    }

    [Fact]
    public async Task TheAckRidesOutATransportFaultAndARedrainSkipsIt()
    {
        var record = new StopDeliveryRecord();
        record.TryClose("tenant-a", "game-a", "room-1", "end-op-1", 5, 99, out _, out _);
        record.ObserveApp("simulator", "app-1");
        var input = new ScriptedRoundInput(new RouteEventsReply());
        var roundEnd = new ScriptedRoundEnd(
            new RpcException(new Status(StatusCode.Unavailable, "the control plane was restarting")),
            new AckDeliveryStoppedReply());
        var drain = new DeliveryStopDrainService(record, null!, null!);

        // The reply was lost, not the ack: the retry lands and the pass reports once.
        Assert.True(await drain.DrainAndAckAsync("room-1", input, roundEnd, CancellationToken.None));
        Assert.Equal(2, roundEnd.Acks.Count);
        Assert.All(roundEnd.Acks, ack => Assert.Equal("end-op-1", ack.EndOperationId));

        // A late arrival re-queues the room: the re-drain disposes again, but the first
        // ack's facts stand — no second ack leaves this process.
        record.RequestDrain("room-1");
        Assert.True(await drain.DrainAndAckAsync("room-1", input, roundEnd, CancellationToken.None));
        Assert.Equal(2, roundEnd.Acks.Count);
        Assert.Equal(2, input.Seen.Count);
    }

    /// <summary>The routing leg as a script: every pass is recorded and answered with the
    /// next queued reply — a pass beyond the script answers empty, the honest shape of a
    /// re-drain after the first pass took the pendings.</summary>
    private sealed class ScriptedRoundInput(params RouteEventsReply[] replies) : RoundInputClient
    {
        private readonly Queue<RouteEventsReply> _replies = new(replies);
        public readonly List<RouteEventsRequest> Seen = new();

        public override AsyncUnaryCall<RouteEventsReply> RouteEventsAsync(
            RouteEventsRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            Seen.Add(request);
            var reply = _replies.Count > 0 ? _replies.Dequeue() : new RouteEventsReply();
            return new AsyncUnaryCall<RouteEventsReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The ack leg as a script: every ack is recorded; an RpcException outcome is
    /// thrown once and the next queued outcome answers the retry.</summary>
    private sealed class ScriptedRoundEnd(params object[] outcomes) : RoundEndClient
    {
        private readonly Queue<object> _outcomes = new(outcomes.Length == 0
            ? [new AckDeliveryStoppedReply { RecordedCloseRouteVersion = 99 }]
            : outcomes);
        public readonly List<AckDeliveryStoppedRequest> Acks = new();

        public override AsyncUnaryCall<AckDeliveryStoppedReply> AckDeliveryStoppedAsync(
            AckDeliveryStoppedRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            Acks.Add(request);
            var outcome = _outcomes.Dequeue();
            if (outcome is RpcException error)
                throw error;
            var reply = (AckDeliveryStoppedReply)outcome;
            return new AsyncUnaryCall<AckDeliveryStoppedReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }
}
