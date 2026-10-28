using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignAdminStoreTests
{
    [Fact]
    public async Task AuditRowsLandOncePerRequestAndCommand()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignAdminStore(database.ConnectionString);

        // The first send acts; the same (request id, command) replays as a duplicate; a new
        // request id or a different command under the same id both act again.
        Assert.True(await store.WriteAuditAsync("req-a", "alice", "ledger line missing",
            "room-r", "RetrySettlement", """{"room":"room-r"}"""));
        Assert.False(await store.WriteAuditAsync("req-a", "alice", "ledger line missing",
            "room-r", "RetrySettlement", """{"room":"room-r"}"""));
        Assert.True(await store.WriteAuditAsync("req-a", "alice", "ledger line missing",
            "room-r", "DrainProcess", """{"process":"game-a"}"""));
        Assert.True(await store.WriteAuditAsync("req-b", "bob", "second attempt",
            "room-r", "RetrySettlement", """{"room":"room-r"}"""));

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var rows = new NpgsqlCommand(
            "SELECT operator,reason,scope,command FROM control.audit ORDER BY audit_id", connection);
        await using var reader = await rows.ExecuteReaderAsync();
        var seen = new List<(string Operator, string Reason, string Scope, string Command)>();
        while (await reader.ReadAsync())
            seen.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        Assert.Equal([
            ("alice", "ledger line missing", "room-r", "RetrySettlement"),
            ("alice", "ledger line missing", "room-r", "DrainProcess"),
            ("bob", "second attempt", "room-r", "RetrySettlement")
        ], seen);
    }

    [Fact]
    public async Task QueueGateUpsertsSurviveAndSuspensionIsQueryable()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var store = new RedesignAdminStore(database.ConnectionString);

        var suspended = await store.SetQueueGateAsync("tenant-a/game-a/rules-1/region-1/simulator/standard",
            true, "alice");
        Assert.True(suspended.Suspended);
        Assert.Equal("alice", suspended.UpdatedBy);
        var read = await store.GetQueueGateAsync(
            "tenant-a/game-a/rules-1/region-1/simulator/standard");
        Assert.NotNull(read);
        Assert.True(read!.Suspended);
        Assert.Contains("tenant-a/game-a/rules-1/region-1/simulator/standard",
            await store.ListSuspendedPartitionsAsync());

        // A later resume flips the same row — the gate is the row's current state, not history.
        var resumed = await store.SetQueueGateAsync(
            "tenant-a/game-a/rules-1/region-1/simulator/standard", false, "bob");
        Assert.False(resumed.Suspended);
        Assert.DoesNotContain("tenant-a/game-a/rules-1/region-1/simulator/standard",
            await store.ListSuspendedPartitionsAsync());
        Assert.Null(await store.GetQueueGateAsync("never/gated"));
    }

    [Fact]
    public async Task TraceRoundAssemblesTheWholeCausalChain()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var store = new RedesignAdminStore(database.ConnectionString);
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "admin-open");
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "room-admin", "live-admin-key");
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a",
            "op-mount-admin");

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        const string room = "room-admin-trace";
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,config_hash)
            VALUES (@room,'tenant-a','game-a','Finishing',1,'pkg','1.0.0','cfg');
            INSERT INTO game.participation(room_id,participant_id,binding_id,role,state)
            VALUES (@room,@streamer,@binding,'Host','Active'),
                   (@room,'guest-p',NULL,'Guest','Active');
            INSERT INTO input.event(event_id,platform,app_id,source_dedup_key,payload_hash,disposition,
                binding_id,payload)
            VALUES ('ev-1','simulator','app-1','gift-1',@hash,'Consumed',@binding,
                    '{"kind":"gift","amount":100}'),
                   ('ev-2','simulator','app-1','gift-2',@hash,'Consumed',@binding,
                    '{"kind":"gift","amount":50}');
            INSERT INTO input.cursor(consumer_scope) VALUES ('room:' || @room);
            INSERT INTO input.delivery(event_id,consumer_scope,room_id,route_version,sequence,state)
            VALUES ('ev-1','room:' || @room,@room,1,1,'Consumed'),
                   ('ev-2','room:' || @room,@room,1,2,'Consumed');
            INSERT INTO game.frame(room_id,tick_id,commit_id,input_refs,state_hash,effects,module_version,
                config_hash)
            VALUES (@room,1,'commit-1','["ev-1","ev-2"]',@hash,
                    '[{"kind":"score","payload_json":"{\"player\":\"p-1\"}"}]','1.0.0','cfg'),
                   (@room,2,'commit-2','[]',@hash,'[]','1.0.0','cfg');
            INSERT INTO infra.outbox(effect_id,kind,payload,status)
            VALUES (@room || '/1/0','score','{"player":"p-1"}','Recorded'),
                   (@room || '/2/0','round.finished','{}','Pending');
            INSERT INTO game.result(room_id,result_version,final_frame,plan_hash,result)
            VALUES (@room,1,2,@hash,'{"winner":"p-1"}');
            INSERT INTO settlement.grant(room_id,result_version,player_id,grant_key,status,operation_id,
                asset_id,delta)
            VALUES (@room,1,'p-1','win','Granted','grant-op-1','score',10),
                   (@room,1,'p-1','mvp','Planned','grant-op-2','score',5);
            INSERT INTO player.ledger(tenant_id,game_id,operation_id,line_id,player_id,asset_id,delta,
                reason,resulting_balance)
            VALUES ('tenant-a','game-a','grant-op-1','grant','p-1','score',10,
                    'settlement:' || @room || '/win',10);
            UPDATE live.binding SET current_room_id=@room WHERE binding_id=@binding;
            INSERT INTO control.ownership(tenant_id,game_id,kind,entity_id,owner_process_instance_id,
                epoch,lease_until,status,version)
            VALUES ('tenant-a','game-a','streamer',@streamer,'streamer-a',2,clock_timestamp()+interval '10 s',
                'Active',1),
                   ('tenant-a','game-a','room',@room,'game-a',1,clock_timestamp()+interval '10 s',
                'Active',1);
            """, connection))
        {
            seed.Parameters.AddWithValue("room", room);
            seed.Parameters.AddWithValue("streamer", streamer.StreamerId);
            seed.Parameters.AddWithValue("binding", binding.BindingId);
            seed.Parameters.AddWithValue("hash", RandomNumberGenerator.GetBytes(4));
            await seed.ExecuteNonQueryAsync();
        }

        // The trace walks the whole chain in order: routed events, consuming frames, durable
        // effects, the result, its manifest and the ledger line the granted one produced.
        var trace = await store.TraceRoundAsync(room);
        Assert.True(trace.Found);
        Assert.Equal("Finishing", trace.Status);
        Assert.Equal(2, trace.Events.Count);
        Assert.Equal("ev-1", trace.Events[0].EventId);
        Assert.Equal(1, trace.Events[0].Sequence);
        Assert.Contains("gift", trace.Events[0].PayloadJson);
        Assert.Equal(2, trace.Frames.Count);
        Assert.Contains("ev-1", trace.Frames[0].InputRefsJson);
        Assert.Contains("score", trace.Frames[0].EffectsJson);
        Assert.Equal(2, trace.Effects.Count);
        Assert.Equal(room + "/1/0", trace.Effects[0].EffectId);
        Assert.Equal("Recorded", trace.Effects[0].Status);
        Assert.Equal(1, trace.ResultVersion);
        Assert.Contains("winner", trace.ResultJson);
        Assert.Equal(2, trace.Grants.Count);
        Assert.Equal("mvp", trace.Grants[0].GrantKey);
        Assert.Equal("Planned", trace.Grants[0].Status);
        Assert.Equal("win", trace.Grants[1].GrantKey);
        Assert.Equal("Granted", trace.Grants[1].Status);
        var line = Assert.Single(trace.Ledger);
        Assert.Equal("grant-op-1", line.OperationId);
        Assert.Equal(10, line.ResultingBalance);

        var roomSnapshot = await store.GetRoomSnapshotAsync(room);
        Assert.True(roomSnapshot.Found);
        Assert.Equal("tenant-a", roomSnapshot.TenantId);
        Assert.Equal(2, roomSnapshot.Participants.Count);
        Assert.NotNull(roomSnapshot.Ownership);
        Assert.Equal(1, roomSnapshot.ResultVersion);
        Assert.Equal(2, roomSnapshot.GrantsTotal);
        Assert.Equal(1, roomSnapshot.GrantsGranted);

        var streamerSnapshot = await store.GetStreamerSnapshotAsync("tenant-a", streamer.StreamerId);
        Assert.True(streamerSnapshot.Found);
        var traced = Assert.Single(streamerSnapshot.Bindings, item => item.BindingId == binding.BindingId);
        Assert.Equal(room, traced.CurrentRoomId);
        Assert.NotNull(streamerSnapshot.Ownership);
        Assert.Equal("streamer-a", streamerSnapshot.Ownership!.OwnerInstanceId);
        Assert.True(streamerSnapshot.Ticket is null);

        // An unknown room and streamer answer found=false, never throw.
        Assert.False((await store.TraceRoundAsync("room-none")).Found);
        Assert.False((await store.GetRoomSnapshotAsync("room-none")).Found);
        Assert.False((await store.GetStreamerSnapshotAsync("tenant-a", "streamer-none")).Found);
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m3_admin_" + Guid.NewGuid().ToString("N");
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
            await Chorus.Data.Redesign.RedesignSchemaMigrator.ApplyAsync(connection);
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
