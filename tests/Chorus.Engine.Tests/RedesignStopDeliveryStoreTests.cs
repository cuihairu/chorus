using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
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

    /// <summary>One mounted binding, one started round and a gateway directory the responder
    /// set reads — the minimum the command's issue fence and set derivation need.</summary>
    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public (string BindingId, string StreamerId) Binding { get; init; }

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
