using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Persistent end operations (FIX-END-01/04): the begin records one operation per round —
/// the business id, the round generation the mounts actually hold, the mount generations —
/// and every later step records its phase and error on the same row. Creation is idempotent
/// by request identity, the phase ladder advances one recorded step at a time, and a begin
/// arriving after the mounts left the room writes nothing: a restarted control plane reads
/// the rows and continues under the same business id, and a late request cannot touch the
/// round the mounts entered next.
/// </summary>
public sealed class RedesignEndOperationStoreTests
{
    [Fact]
    public async Task ADuplicateBeginCreatesExactlyOneOperation()
    {
        await using var arrange = await Arrange.RoundAsync();
        var begin = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        Assert.False(begin.Duplicate);
        Assert.Equal("end-op-1", begin.Operation.EndOperationId);
        Assert.Equal(EndOperationPhases.ResultFrozen, begin.Operation.Phase);
        Assert.Equal(600, begin.Operation.FinalTick);
        Assert.Equal(2, begin.Operation.ResultVersion);
        var mount = Assert.Single(begin.Operation.BindingGenerations);
        Assert.Equal(arrange.Binding.BindingId, mount.BindingId);
        Assert.Equal(1, mount.RoundGeneration);

        // The reply was lost, not the operation: the same begin replays the one row.
        var replay = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);
        Assert.True(replay.Duplicate);
        Assert.Equal(begin.Operation.EndOperationId, replay.Operation.EndOperationId);
        Assert.Equal(EndOperationPhases.ResultFrozen, replay.Operation.Phase);

        // A second business id for the same round refuses: one operation, one coordinator.
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
                "end-op-2", finalTick: 600, resultVersion: 2));
        // The same id naming another room refuses: the id is the round's business identity.
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-2",
                "end-op-1", finalTick: 600, resultVersion: 2));
        Assert.Single(await arrange.EndOperationRowsAsync());
    }

    [Fact]
    public async Task ThePhaseLadderAdvancesOneStepAtATimeAndTheErrorStaysOnTheRow()
    {
        await using var arrange = await Arrange.RoundAsync();
        var begin = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", finalTick: 600, resultVersion: 2);

        // A failure is recorded without leaving the phase — the restart reads the phase and
        // the reason and retries the same step. The open list is the resume list.
        await arrange.Control.RecordEndOperationErrorAsync("tenant-a", "game-a", "end-op-1",
            "The platform close answered OutcomeUnknown; the retry asks the platform first.");
        var recorded = Assert.Single(await arrange.Control.ListOpenEndOperationsAsync());
        Assert.Equal(begin.Operation.EndOperationId, recorded.EndOperationId);
        Assert.Equal(EndOperationPhases.ResultFrozen, recorded.Phase);
        Assert.Contains("OutcomeUnknown", recorded.LastError);

        // The whole recorded sequence, one step at a time under the same business id.
        var ladder = new[]
        {
            (EndOperationPhases.ResultFrozen, EndOperationPhases.ClosingInput),
            (EndOperationPhases.ClosingInput, EndOperationPhases.StopDeliveryIssued),
            (EndOperationPhases.StopDeliveryIssued, EndOperationPhases.DeliveryStopped),
            (EndOperationPhases.DeliveryStopped, EndOperationPhases.PlatformTaskAuthorized),
            (EndOperationPhases.PlatformTaskAuthorized, EndOperationPhases.PlatformClosed),
            (EndOperationPhases.PlatformClosed, EndOperationPhases.Released),
            (EndOperationPhases.Released, EndOperationPhases.Destroyed),
            (EndOperationPhases.Destroyed, EndOperationPhases.Done)
        };
        foreach (var (from, to) in ladder)
        {
            var advanced = await arrange.Control.AdvanceEndOperationPhaseAsync(
                "tenant-a", "game-a", "end-op-1", from, to);
            Assert.Equal(to, advanced.Phase);
        }

        // A step that skips ahead, steps back or names a phase the row never held refuses:
        // a late or reordered step cannot rewrite the recorded progress.
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.AdvanceEndOperationPhaseAsync("tenant-a", "game-a", "end-op-1",
                EndOperationPhases.Done, EndOperationPhases.ResultFrozen));
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.AdvanceEndOperationPhaseAsync("tenant-a", "game-a", "end-op-1",
                EndOperationPhases.ResultFrozen, EndOperationPhases.DeliveryStopped));

        // The finished operation is history: the resume list is empty and the status still
        // answers by room.
        Assert.Empty(await arrange.Control.ListOpenEndOperationsAsync());
        Assert.Equal(begin.Operation.RoomId,
            (await arrange.Control.GetEndOperationAsync("tenant-a", "game-a", "room-1"))!.RoomId);
    }

    [Fact]
    public async Task ALateBeginWhoseMountsLeftTheRoomWritesNothing()
    {
        await using var arrange = await Arrange.RoundAsync();

        // The mount's pointer moved to another room — the round the begin would freeze is
        // over and the mounts entered the next one (FIX-END-04's fence reads the pointer: a
        // moved pointer is the production shape of "moved past").
        await using (var moved = new NpgsqlCommand(
            "UPDATE live.binding SET current_room_id='room-elsewhere' WHERE binding_id=@binding",
            new NpgsqlConnection(arrange.Database.ConnectionString)))
        {
            await moved.Connection.OpenAsync();
            moved.Parameters.AddWithValue("binding", arrange.Binding.BindingId);
            await moved.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<StaleRoundGenerationException>(() =>
            arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
                "end-op-late", finalTick: 600, resultVersion: 2));
        // A room with no participant mounts at all is late the same way.
        await Assert.ThrowsAsync<StaleRoundGenerationException>(() =>
            arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-ghost",
                "end-op-ghost", finalTick: 600, resultVersion: 2));
        Assert.Empty(await arrange.EndOperationRowsAsync());

        // The pointer is back where the round holds it: the begin succeeds and the recorded
        // round generation is the one the mounts actually hold, not a caller claim.
        await using (var restored = new NpgsqlCommand(
            "UPDATE live.binding SET current_room_id='room-1' WHERE binding_id=@binding",
            new NpgsqlConnection(arrange.Database.ConnectionString)))
        {
            await restored.Connection.OpenAsync();
            restored.Parameters.AddWithValue("binding", arrange.Binding.BindingId);
            await restored.ExecuteNonQueryAsync();
        }
        var begin = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-2", finalTick: 600, resultVersion: 2);
        Assert.False(begin.Duplicate);
        Assert.Equal(1, begin.Operation.RoundGeneration);
        Assert.Single(await arrange.EndOperationRowsAsync());
    }

    [Fact]
    public async Task ARoomWhoseMountsCarryDifferentRoundHistoriesStillBegins()
    {
        await using var arrange = await Arrange.MixedHistoryRoundAsync();

        // The two mounts hold different per-binding round counters — production's normal
        // shape — and the begin still records the one operation, taking the evidence the
        // begin found (FIX-END-04 derives; no caller name is needed).
        var begin = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-mixed", finalTick: 300, resultVersion: 1);
        Assert.False(begin.Duplicate);
        Assert.Equal(2, begin.Operation.RoundGeneration);
        var mounts = begin.Operation.BindingGenerations.OrderBy(binding => binding.BindingId).ToList();
        Assert.Equal(2, mounts.Count);
        Assert.Equal(2, mounts.Single(binding => binding.BindingId == arrange.Binding.BindingId).RoundGeneration);
        Assert.Equal(1, mounts.Single(binding => binding.BindingId == arrange.SecondBinding.BindingId).RoundGeneration);
    }

    /// <summary>One mounted binding and one started round — the minimum the end operation's
    /// fence reads.</summary>
    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public (string BindingId, string StreamerId) Binding { get; init; }
        public (string BindingId, string StreamerId) SecondBinding { get; init; }

        public async Task<List<string>> EndOperationRowsAsync()
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            var ids = new List<string>();
            await using var command = new NpgsqlCommand(
                "SELECT end_operation_id FROM control.end_operation", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
            return ids;
        }

        /// <summary>Two mounted bindings in one started round where one mount carries an aborted
        /// round's history — the per-binding counters diverge (2 and 1), the production shape the
        /// end begin derives its round generation from.</summary>
        public static async Task<Arrange> MixedHistoryRoundAsync()
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
                var streamerA = await arrange.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                    "app-1", "end-op-mixed-a");
                var streamerB = await arrange.IdentityStore.ResolveIdentityAsync("tenant-a", "simulator",
                    "app-1", "end-op-mixed-b");
                var liveA = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamerA.StreamerId,
                    "simulator", "app-1", "platform-room", "live-key-mixed-a");
                var liveB = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamerB.StreamerId,
                    "simulator", "app-1", "platform-room", "live-key-mixed-b");
                var sessionA = await arrange.IdentityStore.LoginAsync(streamerA.StreamerId, "gateway-1",
                    "connection-1", RandomNumberGenerator.GetBytes(32));
                var sessionB = await arrange.IdentityStore.LoginAsync(streamerB.StreamerId, "gateway-1",
                    "connection-2", RandomNumberGenerator.GetBytes(32));
                var mountA = await arrange.BindingStore.StartGameplayAsync("tenant-a", liveA,
                    sessionA.SessionId, "game-a", "mount-1");
                var mountB = await arrange.BindingStore.StartGameplayAsync("tenant-a", liveB,
                    sessionB.SessionId, "game-a", "mount-2");

                // The first mount carries an aborted round: its counter already holds 1 when the
                // room below starts, so the two mounts enter with different histories.
                var prior = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room",
                    "room-prior", "gs-1", 60);
                await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-prior", "gs-1", prior.Epoch,
                    [new RoundParticipant(mountA.BindingId, streamerA.StreamerId, "Host")],
                    "1.0.0", "cfg-1", "pkg-1", "start-prior");
                await arrange.RoomStore.AbortRoundAsync("tenant-a", "game-a", "room-prior", "gs-1", prior.Epoch,
                    "prior-round-over", "abort-prior");

                var claim = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-1",
                    "gs-1", 60);
                await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-1", "gs-1", claim.Epoch,
                    [new RoundParticipant(mountA.BindingId, streamerA.StreamerId, "Host"),
                     new RoundParticipant(mountB.BindingId, streamerB.StreamerId, "Guest")],
                    "1.0.0", "cfg-1", "pkg-1", "start-mixed");
                return new Arrange
                {
                    Database = database,
                    RoomStore = arrange.RoomStore,
                    IdentityStore = arrange.IdentityStore,
                    BindingStore = arrange.BindingStore,
                    Control = arrange.Control,
                    Binding = (mountA.BindingId, streamerA.StreamerId),
                    SecondBinding = (mountB.BindingId, streamerB.StreamerId)
                };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
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
                    "app-1", "end-op-open-1");
                var live = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                    "simulator", "app-1", "platform-room", "live-key-end-op");
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
        private readonly string _name = "chorus_end_op_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
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
