using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-01, durable leg: a directly pushed room event is fenced by the room ownership
/// row (tenant, game, owner instance, epoch, live lease, Active) before anything writes;
/// the event, its inbox delivery and the cursor advance commit as one transaction. The
/// event id is the business identity — an exact retry replays the stored receipt as
/// Duplicate, the same id under a different payload conflicts, and a room that moved on
/// (owner, epoch, lease, tenant) refuses visibly without writing anything.
/// </summary>
public sealed class RedesignRoomEventPushStoreTests
{
    private const string Payload = """{"action":"boost","level":3}""";

    [Fact]
    public async Task APushIsFencedByOwnershipAndIdempotentByEventId()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (host, binding, _) = await MountAsync(identityStore, bindingStore, "live-key-push-1");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-push-1", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-push-1", "gs-1", claim.Epoch,
            [new RoundParticipant(binding.BindingId, host.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-push-1");

        var accepted = await inputStore.AcceptRoomEventAsync("tenant-a", "game-a", "room-push-1",
            "gs-1", claim.Epoch, "gift-1", "simulator", "app-1", binding.BindingId, Payload);
        Assert.Equal("gift-1", accepted.EventId);
        Assert.Equal("room-push-1", accepted.RoomId);
        Assert.Equal(1L, accepted.Sequence);
        Assert.Equal(1L, accepted.RouteVersion);
        Assert.False(accepted.Duplicate);

        // The exact retry replays the receipt: one event, one delivery, one sequence.
        var retry = await inputStore.AcceptRoomEventAsync("tenant-a", "game-a", "room-push-1",
            "gs-1", claim.Epoch, "gift-1", "simulator", "app-1", binding.BindingId, Payload);
        Assert.True(retry.Duplicate);
        Assert.Equal(1L, retry.Sequence);
        // The same event id under a different payload is a reuse conflict, not a duplicate.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => inputStore.AcceptRoomEventAsync(
            "tenant-a", "game-a", "room-push-1", "gs-1", claim.Epoch, "gift-1", "simulator",
            "app-1", binding.BindingId, """{"action":"boost","level":9}"""));

        // The fence: a moved owner, epoch, tenant — or no ownership row at all — writes
        // nothing. Each refusal names a room this caller's picture no longer matches.
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => inputStore.AcceptRoomEventAsync(
            "tenant-a", "game-a", "room-push-1", "gs-2", claim.Epoch, "gift-2", "simulator",
            "app-1", binding.BindingId, Payload));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => inputStore.AcceptRoomEventAsync(
            "tenant-a", "game-a", "room-push-1", "gs-1", claim.Epoch + 5, "gift-3", "simulator",
            "app-1", binding.BindingId, Payload));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => inputStore.AcceptRoomEventAsync(
            "tenant-b", "game-a", "room-push-1", "gs-1", claim.Epoch, "gift-4", "simulator",
            "app-1", binding.BindingId, Payload));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => inputStore.AcceptRoomEventAsync(
            "tenant-a", "game-a", "room-unknown", "gs-1", claim.Epoch, "gift-5", "simulator",
            "app-1", binding.BindingId, Payload));
        // A dead lease is as good as no owner: the row is no longer authoritative.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var expire = new NpgsqlCommand(
                "UPDATE control.ownership SET lease_until=clock_timestamp() WHERE kind='room'", connection);
            await expire.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => inputStore.AcceptRoomEventAsync(
            "tenant-a", "game-a", "room-push-1", "gs-1", claim.Epoch, "gift-6", "simulator",
            "app-1", binding.BindingId, Payload));

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            // Exactly one accepted push: the retry wrote nothing, the refusals wrote nothing.
            await using (var events = new NpgsqlCommand("""
                SELECT count(*),count(distinct source_dedup_key) FROM input.event
                WHERE platform='simulator' AND app_id='app-1' AND attributed_room_id='room-push-1'
                """, connection))
            await using (var reader = await events.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using (var deliveries = new NpgsqlCommand("""
                SELECT count(*),max(sequence) FROM input.delivery
                WHERE consumer_scope='room:room-push-1' AND state='Pending'
                """, connection))
            await using (var reader = await deliveries.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            await using (var cursor = new NpgsqlCommand(
                "SELECT next_delivery_sequence FROM input.cursor WHERE consumer_scope='room:room-push-1'",
                connection))
                Assert.Equal(2L, Convert.ToInt64(await cursor.ExecuteScalarAsync()));
        }

        // The receipt replays even after the fence would refuse: a retry of an important
        // input must stay idempotent across ownership changes, not flap with the lease.
        await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-push-1", "gs-9", 60);
        var afterMove = await inputStore.AcceptRoomEventAsync("tenant-a", "game-a", "room-push-1",
            "gs-1", claim.Epoch, "gift-1", "simulator", "app-1", binding.BindingId, Payload);
        Assert.True(afterMove.Duplicate);
        Assert.Equal(1L, afterMove.Sequence);
    }

    [Fact]
    public async Task ABindinglessPushIsRoomScopedAndTerminalRoomsRefuseVisibly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        var (host, binding, _) = await MountAsync(identityStore, bindingStore, "live-key-push-2");
        var (outsider, outsiderBinding, _) = await MountAsync(identityStore, bindingStore, "live-key-push-2x");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-push-2", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-push-2", "gs-1", claim.Epoch,
            [new RoundParticipant(binding.BindingId, host.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-push-2");

        // An empty binding is a room-scoped event: accepted durably, no module seat, and
        // the delivery names the epoch that admitted it.
        var scoped = await inputStore.AcceptRoomEventAsync("tenant-a", "game-a", "room-push-2",
            "gs-1", claim.Epoch, "event-room", "simulator", "app-1", null, Payload);
        Assert.False(scoped.Duplicate);
        Assert.Equal(claim.Epoch, scoped.RouteVersion);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var row = new NpgsqlCommand("""
                SELECT binding_id IS NULL,binding_generation IS NULL,canonical_room_key IS NULL
                FROM input.event WHERE event_id='event-room'
                """, connection))
            await using (var reader = await row.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetBoolean(2));
            }
        }

        // A binding that does not point at this room is an attribution error, not a
        // stale-owner picture: visible, and retrying the same attribution cannot fix it.
        await Assert.ThrowsAsync<RoundAdmissionException>(() => inputStore.AcceptRoomEventAsync(
            "tenant-a", "game-a", "room-push-2", "gs-1", claim.Epoch, "gift-x", "simulator", "app-1",
            outsiderBinding.BindingId, Payload));
        // A room the round left behind has no authoritative owner any more: the abort
        // withdrew the ownership row, so the fence answers stale before the room's own
        // status could even be consulted.
        await roomStore.AbortRoundAsync("tenant-a", "game-a", "room-push-2", "gs-1", claim.Epoch,
            "host-left", "abort-push-2");
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => inputStore.AcceptRoomEventAsync(
            "tenant-a", "game-a", "room-push-2", "gs-1", claim.Epoch, "gift-late", "simulator",
            "app-1", binding.BindingId, Payload));

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var deliveries = new NpgsqlCommand("""
                SELECT count(*) FROM input.delivery WHERE consumer_scope='room:room-push-2'
                """, connection))
                Assert.Equal(1L, Convert.ToInt64(await deliveries.ExecuteScalarAsync()));
        }
    }

    private static async Task<(ResolvedStreamer Streamer, BindingGrant Binding, SessionGrant Session)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1",
            "push-open-" + canonicalKey);
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator", "app-1",
            "platform-room", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        return (streamer, binding, session);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_input_push_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
