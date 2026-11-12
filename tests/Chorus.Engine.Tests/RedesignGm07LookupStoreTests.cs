using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-GM-07's data-plane reads at the store: the dispatch's pre-flight binding read answers
/// a seat's current room and round generation (and absence truthfully), and the effect query
/// answers one operation id from the durable record — routed-and-pending with no tick before
/// the room consumes it, consumed with its tick after, and absence for an id that never
/// existed. No state is invented for an unknown id or an unknown binding.
/// </summary>
public sealed class RedesignGm07LookupStoreTests
{
    private const string Payload = """{"action":"boost","level":1}""";

    [Fact]
    public async Task ALookupAnswersAbsenceAcceptanceAndConsumptionFromTheDurableRecord()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var inputStore = new RedesignRoundInputStore(database.ConnectionString);

        // Absence is truthful before anything exists.
        Assert.False((await inputStore.LookupOperationAsync("gm-lookup-none")).Found);

        var (host, binding, _) = await MountAsync(identityStore, bindingStore, "live-key-gm07-lookup");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room",
            "room-gm07-lookup", "gs-1", 60);

        // The pre-flight read: an unattached binding carries no room and no round yet.
        var free = await inputStore.GetBindingAsync(binding.BindingId);
        Assert.True(free.Found);
        Assert.Null(free.CurrentRoomId);
        Assert.False((await inputStore.GetBindingAsync("binding-gone")).Found);

        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-gm07-lookup", "gs-1", claim.Epoch,
            [new RoundParticipant(binding.BindingId, host.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-gm07-lookup");

        // Admission points the binding at the room and bumps its round generation — the
        // dispatch's pre-flight now resolves a live seat.
        var seated = await inputStore.GetBindingAsync(binding.BindingId);
        Assert.True(seated.Found);
        Assert.Equal("room-gm07-lookup", seated.CurrentRoomId);
        Assert.True(seated.RoundGeneration >= 1, $"saw generation {seated.RoundGeneration}");

        var accepted = await inputStore.AcceptRoomEventAsync("tenant-a", "game-a",
            "room-gm07-lookup", "gs-1", claim.Epoch, "gm-lookup-1", "gm", "gm",
            binding.BindingId, 1, Payload);
        Assert.False(accepted.Duplicate);

        // Accepted, not yet applied: the input is routed with a pending delivery, no tick.
        var pending = await inputStore.LookupOperationAsync("gm-lookup-1");
        Assert.True(pending.Found);
        Assert.Equal("gm", pending.Platform);
        Assert.Equal("Routed", pending.Disposition);
        Assert.Equal("room-gm07-lookup", pending.RoomId);
        Assert.Equal("Pending", pending.DeliveryState);
        Assert.Equal(0L, pending.AppliedTick);

        // The room consumes the input into a committed frame: the same operation id now
        // answers with the tick that ate it.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var consume = new NpgsqlCommand("""
                UPDATE input.delivery SET state='Consumed' WHERE event_id=@event;
                INSERT INTO game.frame(room_id,tick_id,commit_id,input_refs,state_hash,effects,
                    module_version,config_hash)
                VALUES ('room-gm07-lookup',7,'commit-gm07-1',
                    to_jsonb(ARRAY['gm-lookup-1']::text[]),'\x01'::bytea,'[]'::jsonb,'1.0.0','cfg-1')
                """, connection);
            consume.Parameters.AddWithValue("event", "gm-lookup-1");
            await consume.ExecuteNonQueryAsync();
        }
        var applied = await inputStore.LookupOperationAsync("gm-lookup-1");
        Assert.True(applied.Found);
        Assert.Equal("Consumed", applied.DeliveryState);
        Assert.Equal(7L, applied.AppliedTick);

        // Absence stays truthful beside a real operation: an unknown id borrows no facts.
        Assert.False((await inputStore.LookupOperationAsync("gm-lookup-other")).Found);

        await Assert.ThrowsAsync<ArgumentException>(() => inputStore.LookupOperationAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => inputStore.GetBindingAsync(" "));
    }

    private static async Task<(ResolvedStreamer Streamer, BindingGrant Binding, SessionGrant Session)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1",
            "gm07-open-" + canonicalKey);
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator", "app-1",
            "platform-room", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        return (streamer, binding, session);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_gm07_lookup_" + Guid.NewGuid().ToString("N");
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
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
