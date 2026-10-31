using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Persistent end operations (FIX-END-01): the begin records one operation per round —
/// the business id, the round generation, the mount generations — and every later step
/// records its phase and error on the same row. Creation is idempotent by request
/// identity, the phase ladder advances one recorded step at a time, and a begin naming a
/// generation the mounts have moved past writes nothing: a restarted control plane reads
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
            "end-op-1", roundGeneration: 1, finalTick: 600, resultVersion: 2);
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
            "end-op-1", roundGeneration: 1, finalTick: 600, resultVersion: 2);
        Assert.True(replay.Duplicate);
        Assert.Equal(begin.Operation.EndOperationId, replay.Operation.EndOperationId);
        Assert.Equal(EndOperationPhases.ResultFrozen, replay.Operation.Phase);

        // A second business id for the same round refuses: one operation, one coordinator.
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
                "end-op-2", roundGeneration: 1, finalTick: 600, resultVersion: 2));
        // The same id naming another room refuses: the id is the round's business identity.
        await Assert.ThrowsAsync<EndOperationConflictException>(() =>
            arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-2",
                "end-op-1", roundGeneration: 1, finalTick: 600, resultVersion: 2));
        Assert.Single(await arrange.EndOperationRowsAsync());
    }

    [Fact]
    public async Task ThePhaseLadderAdvancesOneStepAtATimeAndTheErrorStaysOnTheRow()
    {
        await using var arrange = await Arrange.RoundAsync();
        var begin = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-1", roundGeneration: 1, finalTick: 600, resultVersion: 2);

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
    public async Task ALateBeginNamingASupersededGenerationWritesNothing()
    {
        await using var arrange = await Arrange.RoundAsync();

        // The mount moved past round generation 1 — the round the late begin names is over.
        await using (var moved = new NpgsqlCommand(
            "UPDATE live.binding SET round_generation=round_generation+1 WHERE binding_id=@binding",
            new NpgsqlConnection(arrange.Database.ConnectionString)))
        {
            await moved.Connection.OpenAsync();
            moved.Parameters.AddWithValue("binding", arrange.Binding.BindingId);
            await moved.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<StaleRoundGenerationException>(() =>
            arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
                "end-op-late", roundGeneration: 1, finalTick: 600, resultVersion: 2));
        // A begin naming a generation the mounts never held refuses the same way.
        await Assert.ThrowsAsync<StaleRoundGenerationException>(() =>
            arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
                "end-op-ahead", roundGeneration: 5, finalTick: 600, resultVersion: 2));
        Assert.Empty(await arrange.EndOperationRowsAsync());

        // The current generation still begins: the fence refuses the late evidence, not the
        // round's own end.
        var begin = await arrange.Control.BeginEndOperationAsync("tenant-a", "game-a", "room-1",
            "end-op-2", roundGeneration: 2, finalTick: 600, resultVersion: 2);
        Assert.False(begin.Duplicate);
        Assert.Equal(2, begin.Operation.RoundGeneration);
        Assert.Single(await arrange.EndOperationRowsAsync());
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
