using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The persisted stop-delivery command (FIX-END-05, architecture 6.4 step 3): the issue derives
/// its CloseRouteVersion from the operation's own recorded facts and freezes it with the row —
/// a repeated issue replays the recorded command, so a late or racing delivery cannot close a
/// newer round. The responder set is every gateway holding a confirmed routing version at
/// issue: an offline gateway keeps its process and confirm rows and stays a pending responder —
/// counted, never skipped.
/// </summary>
public sealed class RedesignStopDeliveryStoreTests
{
    [Fact]
    public async Task IssueStopDeliveryPersistsTheCommandAndTheResponderSet()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        await arrange.RegisterAuthorizedGatewayAsync("gateway-quiet", "https://localhost:7602");
        // A gateway that never confirmed a routing version holds no route authorization: it is
        // not a responder, whatever its process row says.
        await arrange.RegisterGatewayWithoutConfirmAsync("gateway-unconfirmed");

        var (command, duplicate, responders) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        Assert.False(duplicate);
        Assert.Equal("room-1", command.RoomId);
        Assert.Equal(1, command.RoundGeneration);
        Assert.NotEqual(0L, command.CloseRouteVersion);
        Assert.Equal(2, responders.Count);
        Assert.All(responders, responder => Assert.Equal("Pending", responder.State));
        Assert.Contains(responders, responder => responder.ProcessInstanceId == "gateway-live"
            && responder.Endpoint == "https://localhost:7601");
        Assert.Contains(responders, responder => responder.ProcessInstanceId == "gateway-quiet");
        Assert.DoesNotContain(responders, responder => responder.ProcessInstanceId == "gateway-unconfirmed");

        // The reply was lost, not the command: the same issue replays the recorded version and
        // the same responder set — never a second row, never a re-derived version.
        var (replayed, replayDuplicate, replayResponders) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        Assert.True(replayDuplicate);
        Assert.Equal(command.CloseRouteVersion, replayed.CloseRouteVersion);
        Assert.Equal(responders.Count, replayResponders.Count);

        // A gateway that confirmed only after the issue joins no command that already stands:
        // the recorded set is the set.
        await arrange.RegisterAuthorizedGatewayAsync("gateway-late", "https://localhost:7603");
        var (_, _, afterLate) = await arrange.Control.IssueStopDeliveryAsync("tenant-a", "game-a", "end-op-1");
        Assert.DoesNotContain(afterLate, responder => responder.ProcessInstanceId == "gateway-late");
    }

    [Fact]
    public async Task AnOfflineButAuthorizedGatewayStaysInTheResponderSet()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-online", "https://localhost:7601");
        await arrange.RegisterAuthorizedGatewayAsync("gateway-offline", "https://localhost:7602");

        // The gateway died an hour ago: its lifecycle moved on but its rows — the process row
        // and the routing confirm — are never deleted, and the confirm row is the route
        // authorization the responder set reads. Offline is pending, not absent.
        await using (var offline = new NpgsqlCommand("""
            UPDATE control.process SET lifecycle='Exited',
                observed_at=clock_timestamp() - interval '1 hour'
            WHERE process_instance_id='gateway-offline'
            """, new NpgsqlConnection(arrange.Database.ConnectionString)))
        {
            await offline.Connection.OpenAsync();
            await offline.ExecuteNonQueryAsync();
        }

        var (_, _, responders) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        var offlineResponder = responders.Single(responder => responder.ProcessInstanceId == "gateway-offline");
        Assert.Equal("Pending", offlineResponder.State);
        Assert.Equal(2, responders.Count);
    }

    [Fact]
    public async Task ARepeatedCommandCannotCloseANewerRound()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");

        // The first round freezes its result and issues its command while its mounts still
        // point at it — the only order the begin's fence accepts.
        var firstBegin = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        var (first, _, _) = await arrange.Control.IssueStopDeliveryAsync("tenant-a", "game-a", "end-op-1");
        Assert.Equal(firstBegin.Operation.RoundGeneration, first.RoundGeneration);

        // The mount moves on: the first round aborts, the second starts a generation ahead.
        var second = await arrange.NextRoundAsync("room-2");
        var secondBegin = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-2",
            "end-op-2", finalTick: 900, resultVersion: 1);
        var (newer, _, _) = await arrange.Control.IssueStopDeliveryAsync("tenant-a", "game-a", "end-op-2");
        Assert.Equal(second, newer.RoomId);
        Assert.Equal(secondBegin.Operation.RoundGeneration, newer.RoundGeneration);
        Assert.True(newer.RoundGeneration > first.RoundGeneration);

        // The newer round's command names different facts, so it carries a different version —
        // and the first round's recorded command still replays exactly its own version: the
        // re-delivery of a late command can never close the newer round.
        Assert.NotEqual(first.CloseRouteVersion, newer.CloseRouteVersion);
        var (replayed, duplicate, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        Assert.True(duplicate);
        Assert.Equal(first.CloseRouteVersion, replayed.CloseRouteVersion);
        Assert.Equal(first.RoomId, replayed.RoomId);
        Assert.Equal(first.RoundGeneration, replayed.RoundGeneration);
    }

    [Fact]
    public async Task IssueStopDeliveryRefusesADoneOperation()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);

        // The ladder walks to Done; history answers no new command.
        foreach (var (from, to) in new[]
                 {
                     (EndOperationPhases.ResultFrozen, EndOperationPhases.ClosingInput),
                     (EndOperationPhases.ClosingInput, EndOperationPhases.StopDeliveryIssued),
                     (EndOperationPhases.StopDeliveryIssued, EndOperationPhases.DeliveryStopped),
                     (EndOperationPhases.DeliveryStopped, EndOperationPhases.PlatformTaskAuthorized),
                     (EndOperationPhases.PlatformTaskAuthorized, EndOperationPhases.PlatformClosed),
                     (EndOperationPhases.PlatformClosed, EndOperationPhases.Released),
                     (EndOperationPhases.Released, EndOperationPhases.Destroyed),
                     (EndOperationPhases.Destroyed, EndOperationPhases.Done)
                 })
            await arrange.Control.AdvanceEndOperationPhaseAsync("tenant-a", "game-a", "end-op-1", from, to);
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.IssueStopDeliveryAsync("tenant-a", "game-a", "end-op-1"));
        // An operation that never existed refuses the same way — loudly, writing nothing.
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.IssueStopDeliveryAsync("tenant-a", "game-a", "end-op-none"));
        Assert.Null(await arrange.Control.GetStopDeliveryAsync("tenant-a", "game-a", "end-op-1"));
    }

    /// <summary>
    /// The step-4 ack (FIX-END-07): the first confirmation freezes the responder's own
    /// facts at the exact recorded version — identity, watermark and counts — and the ack
    /// is phase-independent and first-answer-wins, so a restarted gateway's re-ack with
    /// honest zeros hears Duplicate and changes nothing the first answer recorded.
    /// </summary>
    [Fact]
    public async Task AnAcknowledgedDeliveryStopRecordsTheResponderFactsIdempotently()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        var (command, _, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();

        // The first confirmation: Pending leaves for Stopped with the gateway's own facts.
        var first = await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a",
            "end-op-1", "room-1", "gateway-live", command.CloseRouteVersion,
            cutoffSequence: 12, drainedOutOfRound: 3, drainedRouted: 1);
        Assert.True(first.Recorded);
        Assert.False(first.Duplicate);
        Assert.Equal(command.CloseRouteVersion, first.RecordedCloseRouteVersion);
        var answered = Assert.Single(await arrange.Control.ListStopDeliveryRespondersAsync(
            "tenant-a", "game-a", "end-op-1"));
        Assert.Equal("gateway-live", answered.ProcessInstanceId);
        Assert.Equal("Stopped", answered.State);
        Assert.NotNull(answered.AnsweredAt);
        Assert.Equal(12, answered.CutoffSequence);
        Assert.Equal(3, answered.DrainedOutOfRound);
        Assert.Equal(1, answered.DrainedRouted);

        // The gateway crashed after its first ack and came back knowing nothing: its re-ack
        // carries honest zeros, and the first answer's watermark and counts stand — nothing
        // a repeat carries can lower them, and the ack's phase never mattered.
        var repeat = await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a",
            "end-op-1", "room-1", "gateway-live", command.CloseRouteVersion,
            cutoffSequence: 0, drainedOutOfRound: 0, drainedRouted: 0);
        Assert.False(repeat.Recorded);
        Assert.True(repeat.Duplicate);
        Assert.Equal(command.CloseRouteVersion, repeat.RecordedCloseRouteVersion);
        var still = Assert.Single(await arrange.Control.ListStopDeliveryRespondersAsync(
            "tenant-a", "game-a", "end-op-1"));
        Assert.Equal("Stopped", still.State);
        Assert.Equal(answered.AnsweredAt, still.AnsweredAt);
        Assert.Equal(12, still.CutoffSequence);
        Assert.Equal(3, still.DrainedOutOfRound);
        Assert.Equal(1, still.DrainedRouted);
    }

    /// <summary>
    /// The ack's refusals (FIX-END-07): a version the command row does not hold, a room the
    /// command does not close, an instance outside the frozen responder set, and a command
    /// that never issued — each refuses loudly and writes nothing.
    /// </summary>
    [Fact]
    public async Task AnAckFromAWrongVersionAnUnknownInstanceOrAnAbsentCommandWritesNothing()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        var (command, _, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();

        var wrongVersion = await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1", "room-1",
                "gateway-live", command.CloseRouteVersion + 7, 0, 0, 0));
        Assert.Contains("version", wrongVersion.Message);
        var wrongRoom = await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1", "room-other",
                "gateway-live", command.CloseRouteVersion, 0, 0, 0));
        Assert.Contains("not room-other", wrongRoom.Message);
        var stranger = await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1", "room-1",
                "gateway-stranger", command.CloseRouteVersion, 0, 0, 0));
        Assert.Contains("not a stop-delivery responder", stranger.Message);
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-none", "room-1",
                "gateway-live", command.CloseRouteVersion, 0, 0, 0));

        // Every refusal left the responder exactly as the issue froze it.
        var responder = Assert.Single(await arrange.Control.ListStopDeliveryRespondersAsync(
            "tenant-a", "game-a", "end-op-1"));
        Assert.Equal("Pending", responder.State);
        Assert.Null(responder.AnsweredAt);
        Assert.Equal(0, responder.CutoffSequence);
        Assert.Equal(0, responder.DrainedOutOfRound);
        Assert.Equal(0, responder.DrainedRouted);
    }

    /// <summary>
    /// The barrier's truth is the live authorization set (FIX-END-08): a gateway that
    /// confirmed its routing only after the issue joins the standing barrier as a pending
    /// responder — a missed instance must not slip past — and until it answers, the phase
    /// parks no matter what the frozen set says.
    /// </summary>
    [Fact]
    public async Task ALateAuthorizedGatewayJoinsTheStandingBarrier()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        var (command, _, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();

        // The late holder: reported and confirmed only after the command stood.
        await arrange.RegisterAuthorizedGatewayAsync("gateway-late", "https://localhost:7602");
        var responders = await arrange.Control.ResolveDeliveryStopBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60);
        Assert.Contains(responders, responder => responder.ProcessInstanceId == "gateway-late"
            && responder.State == "Pending" && responder.HoldsAuthorization);
        Assert.False(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));

        // Every current holder answers; only then the barrier opens.
        await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
            "room-1", "gateway-live", command.CloseRouteVersion, 0, 0, 0);
        await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
            "room-1", "gateway-late", command.CloseRouteVersion, 0, 0, 0);
        Assert.True(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));
    }

    /// <summary>
    /// A responder whose authorization lapsed — its process stopped reporting past the
    /// barrier's stale window — exits the barrier (FIX-END-08), but only past the window:
    /// within it the holder is merely quiet and still owes its answer. The exit is safe
    /// because the standing operation is itself the receiver-side fence that refuses the
    /// lapsed gateway's pushes.
    /// </summary>
    [Fact]
    public async Task ALapsedResponderExitsTheBarrierOnlyPastItsWindow()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-online", "https://localhost:7601");
        await arrange.RegisterAuthorizedGatewayAsync("gateway-quiet", "https://localhost:7602");
        var (command, _, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();
        await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
            "room-1", "gateway-online", command.CloseRouteVersion, 0, 0, 0);

        // The quiet gateway's reports stopped two minutes ago — its route authorization
        // counts as lapsed at a sixty-second window.
        await using (var age = new NpgsqlCommand("""
            UPDATE control.process SET observed_at = clock_timestamp() - interval '120 seconds'
            WHERE process_instance_id='gateway-quiet'
            """, new NpgsqlConnection(arrange.Database.ConnectionString)))
        {
            await age.Connection.OpenAsync();
            await age.ExecuteNonQueryAsync();
        }
        var responders = await arrange.Control.ResolveDeliveryStopBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60);
        Assert.Contains(responders, responder => responder.ProcessInstanceId == "gateway-quiet"
            && !responder.HoldsAuthorization);
        Assert.True(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));

        // The same quiet gateway ten seconds ago — inside the window — still holds: the
        // barrier does not shed a holder for one missed report cycle.
        await using (var warm = new NpgsqlCommand("""
            UPDATE control.process SET observed_at = clock_timestamp() - interval '10 seconds'
            WHERE process_instance_id='gateway-quiet'
            """, new NpgsqlConnection(arrange.Database.ConnectionString)))
        {
            await warm.Connection.OpenAsync();
            await warm.ExecuteNonQueryAsync();
        }
        var within = await arrange.Control.ResolveDeliveryStopBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60);
        Assert.Contains(within, responder => responder.ProcessInstanceId == "gateway-quiet"
            && responder.HoldsAuthorization);
    }

    /// <summary>
    /// The lapse predicate reads the lifecycle the codebase's own liveness reads
    /// (FIX-END-08): a supervisor keeps reporting an exited slot with a fresh observed_at,
    /// so freshness alone could never lapse a crashed agent-supervised gateway — an exited
    /// lifecycle is lapsed the moment the barrier looks, even while its row stays warm.
    /// </summary>
    [Fact]
    public async Task AnAgentReportedExitedGatewayIsLapsedEvenWhileFresh()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        await arrange.RegisterAuthorizedGatewayAsync("gateway-exited", "https://localhost:7602");
        var (command, _, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();
        await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
            "room-1", "gateway-live", command.CloseRouteVersion, 0, 0, 0);

        // The supervisor's report loop keeps the exited slot's row warm — fresh is not live.
        await using (var exited = new NpgsqlCommand("""
            UPDATE control.process SET lifecycle='Exited', observed_at = clock_timestamp()
            WHERE process_instance_id='gateway-exited'
            """, new NpgsqlConnection(arrange.Database.ConnectionString)))
        {
            await exited.Connection.OpenAsync();
            await exited.ExecuteNonQueryAsync();
        }
        var responders = await arrange.Control.ResolveDeliveryStopBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60);
        Assert.Contains(responders, responder => responder.ProcessInstanceId == "gateway-exited"
            && !responder.HoldsAuthorization);
        Assert.True(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));
    }

    /// <summary>
    /// A wrong-version ack cannot open the barrier (FIX-END-08): the ack validates against
    /// the command row before any write, so the responder stays Pending and the barrier —
    /// whose completeness predicate reads the Stopped rows themselves — parks.
    /// </summary>
    [Fact]
    public async Task AWrongVersionAckParksTheBarrier()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        var (command, _, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();

        var wrong = await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
                "room-1", "gateway-live", command.CloseRouteVersion + 1, 0, 0, 0));
        Assert.Contains("version", wrong.Message);
        var responders = await arrange.Control.ResolveDeliveryStopBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60);
        Assert.Contains(responders, responder => responder.ProcessInstanceId == "gateway-live"
            && responder.State == "Pending" && responder.HoldsAuthorization);
        Assert.False(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));
    }

    /// <summary>
    /// An old incarnation's confirmation is not its successor's (FIX-END-08): a restart
    /// mints a new instance id whose authorization is its own row, and the barrier reads
    /// the live set — the successor's Pending parks the phase until the successor itself
    /// answers, however the old instance's row stands.
    /// </summary>
    [Fact]
    public async Task AnOldIncarnationStoppedDoesNotSatisfyItsFreshSuccessor()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-old", "https://localhost:7601");
        var (command, _, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();
        await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
            "room-1", "gateway-old", command.CloseRouteVersion, 0, 0, 0);

        // The restart: a fresh instance id reporting and confirming under its own name.
        await arrange.RegisterAuthorizedGatewayAsync("gateway-new", "https://localhost:7602");
        var responders = await arrange.Control.ResolveDeliveryStopBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60);
        Assert.Contains(responders, responder => responder.ProcessInstanceId == "gateway-old"
            && responder.State == "Stopped" && responder.HoldsAuthorization);
        Assert.Contains(responders, responder => responder.ProcessInstanceId == "gateway-new"
            && responder.State == "Pending");
        Assert.False(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));

        // The successor's own answer — and only it — opens the barrier.
        await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
            "room-1", "gateway-new", command.CloseRouteVersion, 0, 0, 0);
        Assert.True(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));
    }

    /// <summary>
    /// The ack leg carries the barrier's completeness itself (FIX-END-08): a holder that
    /// authorized after the issue may ack before any coordinator pass inserted its row —
    /// its ack proves possession of the command (the room, the operation, the exact frozen
    /// version), so the confirmation records itself. An instance without route
    /// authorization remains a stranger.
    /// </summary>
    [Fact]
    public async Task AnAckFromALateAuthorizedHolderInsertsItsOwnResponderRow()
    {
        await using var arrange = await Arrange.RoundAsync();
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        var (command, _, _) = await arrange.Control.IssueStopDeliveryAsync(
            "tenant-a", "game-a", "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();

        // No resolve pass ran: the late holder's row does not exist, its ack creates it.
        await arrange.RegisterAuthorizedGatewayAsync("gateway-late", "https://localhost:7602");
        var recorded = await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a",
            "end-op-1", "room-1", "gateway-late", command.CloseRouteVersion, 7, 2, 0);
        Assert.True(recorded.Recorded);
        Assert.Equal(command.CloseRouteVersion, recorded.RecordedCloseRouteVersion);
        var inserted = (await arrange.Control.ListStopDeliveryRespondersAsync(
            "tenant-a", "game-a", "end-op-1"))
            .Single(responder => responder.ProcessInstanceId == "gateway-late");
        Assert.Equal("Stopped", inserted.State);
        Assert.Equal("https://localhost:7602", inserted.Endpoint);
        Assert.Equal(7, inserted.CutoffSequence);

        // A reported gateway without a routing confirm holds no route authorization: its
        // ack is a stranger's and refuses.
        await arrange.RegisterGatewayWithoutConfirmAsync("gateway-unconfirmed");
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
                "room-1", "gateway-unconfirmed", command.CloseRouteVersion, 0, 0, 0));
    }

    /// <summary>One mounted binding, one started round and a gateway directory the responder
    /// set reads — the minimum the command's issue fence and set derivation need.</summary>
    /// <summary>
    /// FIX-END-09: the platform end task is recorded early — durably bound to the standing
    /// operation's barrier credential — but no attempt may begin until the ladder reaches
    /// PlatformTaskAuthorized. The listing stays honest (the executor asks its own question
    /// before any lease), and the store's BeginAttempt refusal is the same predicate read
    /// authoritatively: an executor that skipped its pre-check still cannot issue the call.
    /// </summary>
    [Fact]
    public async Task APlatformEndTaskWaitsForTheBarrierBeforeItsFirstAttempt()
    {
        await using var arrange = await Arrange.RoundAsync();
        var tasks = new RedesignPlatformTaskStore(arrange.Database.ConnectionString);
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        await arrange.Control.IssueStopDeliveryAsync("tenant-a", "game-a", "end-op-1");

        // The coordinator's recording, as the ClosingInput step makes it: one task per
        // binding, bound to the operation, replayed idempotently by a restart's re-enqueue.
        var task = await tasks.EnqueueDesiredAsync(arrange.Binding.BindingId, "end-op-1",
            """{"kind":"end-round","roomId":"room-1"}""", barrierEndOperationId: "end-op-1");
        Assert.Equal("end-op-1", task.BarrierEndOperationId);
        var replay = await tasks.EnqueueDesiredAsync(arrange.Binding.BindingId, "end-op-1",
            """{"kind":"end-round","roomId":"room-1"}""", barrierEndOperationId: "end-op-1");
        Assert.True(replay.Duplicate);

        // The listing is the lease protocol's view, not the ladder's: the executor sees the
        // task and must refuse it itself. The barrier answers not-authorized while the ladder
        // stands below the authorization phase.
        Assert.Contains(await tasks.ListReadyTasksAsync(16),
            listed => listed.TaskId == task.TaskId && listed.BarrierEndOperationId == "end-op-1");
        var waiting = await tasks.GetBarrierAuthorizationAsync(task.TaskId);
        Assert.False(waiting.Authorized);
        Assert.Equal("ResultFrozen", waiting.Phase);

        // The authoritative refusal: a lease may be taken (the protocol's own bookkeeping),
        // but no attempt — no external call — may begin, at any phase below the barrier's
        // completion.
        await tasks.AcquireLeaseAsync(task.TaskId, "gateway-live", TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<TaskBarrierException>(() =>
            tasks.BeginAttemptAsync(task.TaskId, "gateway-live", TimeSpan.FromSeconds(30)));
        await arrange.ToStopDeliveryIssuedAsync();
        await Assert.ThrowsAsync<TaskBarrierException>(() =>
            tasks.BeginAttemptAsync(task.TaskId, "gateway-live", TimeSpan.FromSeconds(30)));

        // The barrier completes and the ladder authorizes: the same attempt call now begins,
        // and the recorded credential answers authorized at the phase it stands.
        await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
            "room-1", "gateway-live",
            (await arrange.Control.GetStopDeliveryAsync("tenant-a", "game-a", "end-op-1"))!.CloseRouteVersion,
            0, 0, 0);
        Assert.True(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));
        await arrange.Control.AdvanceEndOperationPhaseAsync("tenant-a", "game-a", "end-op-1",
            EndOperationPhases.DeliveryStopped, EndOperationPhases.PlatformTaskAuthorized);
        var authorized = await tasks.GetBarrierAuthorizationAsync(task.TaskId);
        Assert.True(authorized.Authorized);
        Assert.Equal("PlatformTaskAuthorized", authorized.Phase);
        var attempt = await tasks.BeginAttemptAsync(task.TaskId, "gateway-live",
            TimeSpan.FromSeconds(30));
        Assert.Equal(RedesignPlatformTaskStore.Executing, attempt.Status);
    }

    /// <summary>
    /// The credential's reading is scoped and durable (FIX-END-09): an operation that never
    /// stood answers not-authorized however fresh the task is, an ungated launch task answers
    /// authorized — the gate is scoped to the end tasks that carry the binding — and once the
    /// ladder passes the authorization the answer stays authorized through every later phase,
    /// so a retry that outlives the sequence's completion still lands.
    /// </summary>
    [Fact]
    public async Task TheBarrierCredentialIsScopedDurableAndMonotone()
    {
        await using var arrange = await Arrange.RoundAsync();
        var tasks = new RedesignPlatformTaskStore(arrange.Database.ConnectionString);
        await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        await arrange.RegisterAuthorizedGatewayAsync("gateway-live", "https://localhost:7601");
        await arrange.Control.IssueStopDeliveryAsync("tenant-a", "game-a", "end-op-1");

        // A barrier id no standing operation carries: no credential, no authorization.
        var orphan = await tasks.EnqueueDesiredAsync(arrange.Binding.BindingId, "end-op-none",
            """{"kind":"end-round"}""", barrierEndOperationId: "end-op-none");
        var refused = await tasks.GetBarrierAuthorizationAsync(orphan.TaskId);
        Assert.False(refused.Authorized);
        Assert.Equal("", refused.Phase);

        // A launch-time task carries no binding to any end operation: not this gate's business.
        var launch = await tasks.EnqueueDesiredAsync(arrange.Binding.BindingId, "op-screen",
            """{"kind":"screen-link","state":"active"}""");
        var untouched = await tasks.GetBarrierAuthorizationAsync(launch.TaskId);
        Assert.True(untouched.Authorized);
        Assert.Equal("", untouched.Phase);

        // Past the authorization the answer never retreats: the platform call may land late
        // in the sequence — through the finalize, the destroy, even past Done — and the
        // executor's question still answers yes.
        var task = await tasks.EnqueueDesiredAsync(arrange.Binding.BindingId, "end-op-1",
            """{"kind":"end-round"}""", barrierEndOperationId: "end-op-1");
        await arrange.ToStopDeliveryIssuedAsync();
        await arrange.Control.AcknowledgeStopDeliveryAsync("tenant-a", "game-a", "end-op-1",
            "room-1", "gateway-live",
            (await arrange.Control.GetStopDeliveryAsync("tenant-a", "game-a", "end-op-1"))!.CloseRouteVersion,
            0, 0, 0);
        Assert.True(await arrange.Control.AdvanceDeliveryStoppedBarrierAsync(
            "tenant-a", "game-a", "end-op-1", staleSeconds: 60));
        foreach (var (from, to) in new[]
        {
            (EndOperationPhases.DeliveryStopped, EndOperationPhases.PlatformTaskAuthorized),
            (EndOperationPhases.PlatformTaskAuthorized, EndOperationPhases.PlatformClosed),
            (EndOperationPhases.PlatformClosed, EndOperationPhases.Released),
            (EndOperationPhases.Released, EndOperationPhases.Destroyed),
            (EndOperationPhases.Destroyed, EndOperationPhases.Done)
        })
            await arrange.Control.AdvanceEndOperationPhaseAsync("tenant-a", "game-a", "end-op-1", from, to);
        var late = await tasks.GetBarrierAuthorizationAsync(task.TaskId);
        Assert.True(late.Authorized);
        Assert.Equal("Done", late.Phase);
    }

    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public (string BindingId, string StreamerId) Binding { get; init; }

        /// <summary>Walks the operation to the phase the delivery-stop barrier drives —
        /// the store facts stand in for the coordinator's ladder.</summary>
        public async Task ToStopDeliveryIssuedAsync()
        {
            await Control.AdvanceEndOperationPhaseAsync("tenant-a", "game-a", "end-op-1",
                EndOperationPhases.ResultFrozen, EndOperationPhases.ClosingInput);
            await Control.AdvanceEndOperationPhaseAsync("tenant-a", "game-a", "end-op-1",
                EndOperationPhases.ClosingInput, EndOperationPhases.StopDeliveryIssued);
        }

        /// <summary>A gateway holding route authorization: a process row under a
        /// platform-gateway deployment plus a confirmed routing version.</summary>
        public async Task RegisterAuthorizedGatewayAsync(string instanceId, string endpoint)
        {
            await RegisterGatewayWithoutConfirmAsync(instanceId, endpoint);
            await Control.ConfirmRoutingVersionAsync(instanceId, 41);
        }

        /// <summary>A platform-gateway process without a routing confirm: reported, but holding
        /// no route authorization yet.</summary>
        public async Task RegisterGatewayWithoutConfirmAsync(string instanceId, string? endpoint = null)
        {
            await Control.RegisterNodeAsync("node-gw", "agent-gw", "{}");
            var accepted = await Control.ReportProcessAsync("node-gw", instanceId,
                "node-gw/platform-gateway", "platform-gateway", instanceId, reportSequence: 1,
                lifecycle: "Running", readiness: "Ready", health: "Healthy", admission: "Accepting",
                entityRecovery: "Steady", startedAtUtc: DateTime.UtcNow, endpoint: endpoint,
                selfReport: true);
            Assert.True(accepted.Accepted, $"The gateway {instanceId} never reported: {accepted.EntityRecovery}");
        }

        /// <summary>The mount's next round: the first room aborts (releasing the pointer) and
        /// the named room starts a generation ahead — the newer round a late command must not
        /// close.</summary>
        public async Task<string> NextRoundAsync(string roomId)
        {
            var first = await Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1",
                "gs-1", 60);
            await RoomStore.AbortRoundAsync("tenant-a", "game-a", "room-1", "gs-1", first.Epoch,
                "round-over", "abort-room-1");
            var claim = await Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", roomId,
                "gs-1", 60);
            await RoomStore.StartRoundAsync("tenant-a", "game-a", roomId, "gs-1", claim.Epoch,
                [new RoundParticipant(Binding.BindingId, Binding.StreamerId, "Host")],
                "1.0.0", "cfg-1", "pkg-1", "start-" + roomId);
            return roomId;
        }

        public static async Task<Arrange> RoundAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                var arrange = new Arrange
                {
                    Database = database,
                    RoomStore = new RedesignRoomStore(database.ConnectionString),
                    IdentityStore = new RedesignIdentityStore(database.ConnectionString),
                    BindingStore = new RedesignBindingStore(database.ConnectionString),
                    Control = new RedesignControlStore(database.ConnectionString)
                };
                var streamer = await arrange.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                    "app-1", "stop-delivery-open-1");
                var live = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                    "simulator", "app-1", "platform-room", "live-key-stop-delivery");
                var session = await arrange.IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                    "connection-1", RandomNumberGenerator.GetBytes(32));
                var binding = await arrange.BindingStore.StartGameplayAsync("tenant-a", live,
                    session.SessionId, "game-a", "mount-1");
                var claim = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1",
                    "gs-1", 60);
                await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
                    [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
                    "1.0.0", "cfg-1", "pkg-1", "start-1");
                return new Arrange
                {
                    Database = database,
                    RoomStore = arrange.RoomStore,
                    IdentityStore = arrange.IdentityStore,
                    BindingStore = arrange.BindingStore,
                    Control = arrange.Control,
                    Binding = (binding.BindingId, streamer.StreamerId)
                };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_stop_del_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin))
                await drop.ExecuteNonQueryAsync();
        }
    }
}
