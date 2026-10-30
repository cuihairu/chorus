using System.Security.Cryptography;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignBindingStoreTests
{
    [Fact]
    public async Task LiveSlotIsCrossGameAndHoldsOneActiveBindingAcrossSwitches()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);

        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "binding-open-id");
        var other = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "other-open-id");
        var foreign = await identityStore.ResolveIdentityAsync("tenant-b", "simulator", "app-1", "foreign-open-id");

        // Opening is idempotent; the canonical key cannot be re-bound to another tenant or streamer.
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "room-1", "live-key-1");
        Assert.Equal(live, await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "room-1", "live-key-1"));
        await Assert.ThrowsAsync<BindingDeniedException>(() => bindingStore.OpenLiveSessionAsync("tenant-b",
            foreign.StreamerId, "simulator", "app-1", "room-1", "live-key-1"));
        await Assert.ThrowsAsync<BindingDeniedException>(() => bindingStore.OpenLiveSessionAsync("tenant-a",
            other.StreamerId, "simulator", "app-1", "room-1", "live-key-1"));
        Assert.Equal(live, await bindingStore.GetLiveSessionIdAsync("tenant-a", streamer.StreamerId));
        Assert.Null(await bindingStore.GetActiveBindingAsync("tenant-a", streamer.StreamerId));

        var secret = RandomNumberGenerator.GetBytes(32);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1", secret);

        // First mount; an exact retry returns the original grant.
        var first = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        Assert.Equal(1, first.Generation);
        Assert.Equal("Ready", first.State);
        Assert.False(first.Duplicate);
        var retried = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        Assert.True(retried.Duplicate);
        Assert.Equal(first.BindingId, retried.BindingId);
        Assert.Equal(1, retried.Generation);

        // The same operation id with a different request conflicts instead of switching.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => bindingStore.StartGameplayAsync(
            "tenant-a", live, session.SessionId, "game-b", "op-1"));

        // A cross-GameId switch closes the old binding; only one stays active on the shared slot.
        var second = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-b", "op-2");
        Assert.Equal(2, second.Generation);
        Assert.NotEqual(first.BindingId, second.BindingId);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var active = new NpgsqlCommand("""
                SELECT count(*) FROM live.binding WHERE live_session_id=@live
                  AND state IN ('Starting','Ready','Running','AwaitingPlatformClose')
                """, connection))
            {
                active.Parameters.AddWithValue("live", live);
                Assert.Equal(1, Convert.ToInt32(await active.ExecuteScalarAsync()));
            }
            await using (var closed = new NpgsqlCommand(
                "SELECT state FROM live.binding WHERE binding_id=@binding", connection))
            {
                closed.Parameters.AddWithValue("binding", first.BindingId);
                Assert.Equal("Closed", await closed.ExecuteScalarAsync());
            }
            await using (var slot = new NpgsqlCommand("""
                SELECT generation,active_binding_id FROM live.slot
                WHERE platform='simulator' AND canonical_live_session_key='live-key-1'
                """, connection))
            await using (var reader = await slot.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(second.BindingId, reader.GetString(1));
            }
        }
        var status = await bindingStore.GetActiveBindingAsync("tenant-a", streamer.StreamerId);
        Assert.NotNull(status);
        Assert.Equal("game-b", status!.GameId);
        Assert.Equal(2, status.Generation);

        // Only the current control session may switch; a taken-over session is fenced in-transaction.
        var takeover = await identityStore.LoginAsync(streamer.StreamerId, "gateway-2", "connection-2",
            RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<SessionRevokedException>(() => bindingStore.StartGameplayAsync(
            "tenant-a", live, session.SessionId, "game-a", "op-3"));
        var third = await bindingStore.StartGameplayAsync("tenant-a", live, takeover.SessionId, "game-a", "op-4");
        Assert.Equal(3, third.Generation);

        // A live session that does not exist cannot start gameplay.
        await Assert.ThrowsAsync<BindingDeniedException>(() => bindingStore.StartGameplayAsync("tenant-a",
            "missing-live", takeover.SessionId, "game-a", "op-5"));
    }

    [Fact]
    public async Task MidSwitchCrashKeepsOldMountAndRetryWithSameOperationConverges()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        // Restart fault injection: the switch dies after closing the old binding, before commit.
        var armed = false;
        var bindingStore = new RedesignBindingStore(database.ConnectionString,
            () =>
            {
                if (armed) throw new InvalidOperationException("simulated process death mid-switch");
            });

        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "crash-open-id");
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "room-1", "live-key-1");
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var first = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        Assert.Equal(1, first.Generation);

        // The crash rolls the whole receipt transaction back: the old mount stays the only active one.
        armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bindingStore.StartGameplayAsync(
            "tenant-a", live, session.SessionId, "game-b", "op-2"));
        armed = false;
        var survived = await bindingStore.GetActiveBindingAsync("tenant-a", streamer.StreamerId);
        Assert.NotNull(survived);
        Assert.Equal(first.BindingId, survived!.BindingId);
        Assert.Equal("game-a", survived.GameId);
        Assert.Equal(1, survived.Generation);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var slot = new NpgsqlCommand("""
                SELECT generation,active_binding_id FROM live.slot
                WHERE platform='simulator' AND canonical_live_session_key='live-key-1'
                """, connection))
            await using (var reader = await slot.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(first.BindingId, reader.GetString(1));
            }
        }

        // The same operation id retries cleanly (its receipt claim rolled back too) and converges.
        var second = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-b", "op-2");
        Assert.Equal(2, second.Generation);
        Assert.False(second.Duplicate);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var active = new NpgsqlCommand("""
                SELECT game_id,generation FROM live.binding WHERE live_session_id=@live
                  AND state IN ('Starting','Ready','Running','AwaitingPlatformClose')
                """, connection))
            {
                active.Parameters.AddWithValue("live", live);
                await using var reader = await active.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("game-b", reader.GetString(0));
                Assert.Equal(2L, reader.GetInt64(1));
                Assert.False(await reader.ReadAsync()); // exactly one active binding after recovery
            }
        }
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m1_binding_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
