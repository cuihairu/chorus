using System.Text.Json;
using Chorus.ControlServer;
using Chorus.Data;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The participation-commit dispatch store (FIX-LINK-04): the control plane claims each
/// round.started outbox event with a lease, resolves the participant set to the owning
/// StreamerServer instances, and retires the event only once delivered. A claim is atomic and
/// reclaimable after a lease lapse (a crashed dispatcher's event is never lost), a failed
/// dispatch returns to the pending queue, and the target resolution joins the durable
/// participation set to the mount, streamer, ownership and process report — skipping a
/// participant whose mount no longer points at the room.
/// </summary>
public sealed class RedesignParticipationDispatchStoreTests
{
    [Fact]
    public async Task AClaimTakesPendingAndCompletesDone()
    {
        await using var arrange = await Arrange.CreateAsync();
        await arrange.InsertRoundStartedAsync("round-1", "tenant-a", "game-a", 2,
            [("binding-a", 3L)]);

        var claimed = await arrange.Control.ClaimRoundStartedAsync(32);
        var single = Assert.Single(claimed);
        Assert.Equal("round-1/0/start", single.EffectId);
        Assert.Equal("round-1", single.RoomId);
        Assert.Equal("tenant-a", single.TenantId);
        Assert.Equal("game-a", single.GameId);
        Assert.Equal(2, single.Epoch);
        var participant = Assert.Single(single.Participants);
        Assert.Equal("binding-a", participant.BindingId);
        Assert.Equal(3, participant.RoundGeneration);
        // The claim took the event: a second claim sees nothing.
        Assert.Empty(await arrange.Control.ClaimRoundStartedAsync(32));
        Assert.Equal("Dispatching", await arrange.OutboxStatusAsync("round-1/0/start"));

        await arrange.Control.CompleteRoundStartedAsync(single.EffectId, delivered: true);
        Assert.Equal("Done", await arrange.OutboxStatusAsync("round-1/0/start"));
        Assert.Empty(await arrange.Control.ClaimRoundStartedAsync(32));
    }

    [Fact]
    public async Task AStrandedClaimIsReclaimableOnlyAfterItsLeaseLapses()
    {
        await using var arrange = await Arrange.CreateAsync();
        await arrange.InsertRoundStartedAsync("round-2", "tenant-a", "game-a", 1,
            [("binding-a", 1L)]);
        await arrange.SetStatusAsync("round-2/0/start", "Dispatching",
            nextAttempt: DateTime.UtcNow.AddSeconds(10));
        // A live lease is another dispatcher's event in flight: never reclaimed.
        Assert.Empty(await arrange.Control.ClaimRoundStartedAsync(32));

        await arrange.SetStatusAsync("round-2/0/start", "Dispatching",
            nextAttempt: DateTime.UtcNow.AddSeconds(-31));
        // The lapsed lease is a crashed dispatcher's event: reclaimable, and only once.
        Assert.Single(await arrange.Control.ClaimRoundStartedAsync(32));
        Assert.Empty(await arrange.Control.ClaimRoundStartedAsync(32));
    }

    [Fact]
    public async Task AFailedDispatchReturnsToPending()
    {
        await using var arrange = await Arrange.CreateAsync();
        await arrange.InsertRoundStartedAsync("round-3", "tenant-a", "game-a", 1,
            [("binding-a", 1L)]);
        var claimed = Assert.Single(await arrange.Control.ClaimRoundStartedAsync(32));

        await arrange.Control.CompleteRoundStartedAsync(claimed.EffectId, delivered: false);
        Assert.Equal("Pending", await arrange.OutboxStatusAsync("round-3/0/start"));
        Assert.Single(await arrange.Control.ClaimRoundStartedAsync(32));
    }

    [Fact]
    public async Task ResolveJoinsTheMountToItsOwningInstance()
    {
        await using var arrange = await Arrange.CreateAsync();
        await arrange.MountAsync("streamer-1", "binding-a", "session-1", "round-4", "round-4");
        await arrange.ClaimStreamerAsync("streamer-1", "ss-instance-1", "https://ss-1:7502");

        var target = Assert.Single(await arrange.Control.ResolveParticipationTargetsAsync("round-4"));
        Assert.Equal("streamer-1", target.StreamerId);
        Assert.Equal("binding-a", target.BindingId);
        Assert.Equal(1, target.BindingGeneration);
        Assert.Equal(1, target.RoundGeneration);
        Assert.Equal("ss-instance-1", target.OwnerInstanceId);
        Assert.Equal("https://ss-1:7502", target.Endpoint);
    }

    [Fact]
    public async Task AMootParticipantWhosePointerMovedIsSkipped()
    {
        await using var arrange = await Arrange.CreateAsync();
        // The mount still participates but its round pointer was released (moved away): the
        // commit no longer describes this streamer, so the dispatch must not deliver it.
        await arrange.MountAsync("streamer-2", "binding-b", "session-2", "round-5", currentRoomId: null);
        await arrange.ClaimStreamerAsync("streamer-2", "ss-instance-2", "https://ss-2:7502");

        Assert.Empty(await arrange.Control.ResolveParticipationTargetsAsync("round-5"));
    }

    [Fact]
    public async Task AnUnknownOwnerKeepsTheEndpointNull()
    {
        await using var arrange = await Arrange.CreateAsync();
        // The mount participates in the room and points at it, but no ownership claim names a
        // StreamerServer instance: the target is retained with a null endpoint — the dispatcher
        // retries rather than guess an address.
        await arrange.MountAsync("streamer-3", "binding-c", "session-3", "round-6", "round-6");

        var target = Assert.Single(await arrange.Control.ResolveParticipationTargetsAsync("round-6"));
        Assert.Equal("streamer-3", target.StreamerId);
        Assert.Null(target.OwnerInstanceId);
        Assert.Null(target.Endpoint);
    }

    private sealed class Arrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignControlStore Control = null!;

        public static async Task<Arrange> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                return new Arrange { Database = database, Control = new RedesignControlStore(database.ConnectionString) };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async Task InsertRoundStartedAsync(string roomId, string tenantId, string gameId,
            long epoch, (string binding, long generation)[] participants)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO infra.outbox(effect_id,kind,payload,status)
                VALUES (@effect,'round.started',@payload::jsonb,'Pending')
                """, connection);
            command.Parameters.AddWithValue("effect", $"{roomId}/0/start");
            // The exact anonymous shape the commit writes, so the parse path is exercised
            // against the real wire format (named BindingId/Generation participant objects).
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new
            {
                RoomId = roomId, TenantId = tenantId, GameId = gameId, Epoch = epoch,
                Participants = participants.Select(p => new { BindingId = p.binding, Generation = p.generation })
            }));
            await command.ExecuteNonQueryAsync();
        }

        public async Task SetStatusAsync(string effectId, string status, DateTime? nextAttempt = null)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE infra.outbox SET status=@status,next_attempt=@next WHERE effect_id=@effect",
                connection);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("next", (object?)nextAttempt ?? DBNull.Value);
            command.Parameters.AddWithValue("effect", effectId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> OutboxStatusAsync(string effectId)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT status FROM infra.outbox WHERE effect_id=@effect", connection);
            command.Parameters.AddWithValue("effect", effectId);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>Mounts a streamer into a room: a live session + binding (generation 1,
        /// round 1, pointing at <paramref name="currentRoomId"/> when non-null) plus the room and
        /// its active participation. A null pointer (the moot case) still commits the room and
        /// participation — only the binding's round pointer was released.</summary>
        public async Task MountAsync(string streamerId, string bindingId, string sessionId,
            string roomId, string? currentRoomId)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO auth.streamer_state(streamer_id,tenant_id) VALUES (@streamer,'tenant-a');
                INSERT INTO live.session(live_session_id,tenant_id,streamer_id,platform,app_id,
                    platform_room_key,started_at)
                    VALUES (@session,'tenant-a',@streamer,'simulator','app-1','key-'||@session,clock_timestamp());
                INSERT INTO live.binding(binding_id,live_session_id,tenant_id,game_id,generation,
                    state,current_room_id,round_generation)
                    VALUES (@binding,@session,'tenant-a','game-a',1,'Ready',@current,1);
                INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,
                    module_version,config_hash)
                    VALUES (@room,'tenant-a','game-a','Preparing',1,'pkg','mod','cfg');
                INSERT INTO game.participation(room_id,participant_id,binding_id,role,state)
                    VALUES (@room,@streamer,@binding,'host','Active');
                """, connection);
            command.Parameters.AddWithValue("streamer", streamerId);
            command.Parameters.AddWithValue("session", sessionId);
            command.Parameters.AddWithValue("binding", bindingId);
            command.Parameters.AddWithValue("room", roomId);
            command.Parameters.AddWithValue("current", (object?)currentRoomId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Claims the streamer for a StreamerServer instance and reports that instance's
        /// serving endpoint, so the dispatch resolution chain is complete.</summary>
        public async Task ClaimStreamerAsync(string streamerId, string instanceId, string endpoint)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO control.node(node_id,agent_instance_id) VALUES ('node-1','agent-1');
                INSERT INTO control.deployment(slot_id,node_id,role,desired_version,plan_generation,
                    desired_state,package_hash)
                    VALUES ('slot-ss','node-1','streamer-server','v',1,'Active','pkg');
                INSERT INTO control.process(process_instance_id,slot_id,node_id,agent_instance_id,
                    report_sequence,lifecycle,readiness,health,admission,started_at,endpoint)
                    VALUES (@instance,'slot-ss','node-1','agent-1',0,'Started','Ready','Healthy',
                        'Accepting',clock_timestamp(),@endpoint);
                INSERT INTO control.ownership(kind,entity_id,owner_process_instance_id,epoch,
                    lease_until,status,version,tenant_id,game_id)
                    VALUES ('streamer',@streamer,@instance,1,clock_timestamp()+interval '15 seconds',
                        'Active',1,'tenant-a','game-a');
                """, connection);
            command.Parameters.AddWithValue("streamer", streamerId);
            command.Parameters.AddWithValue("instance", instanceId);
            command.Parameters.AddWithValue("endpoint", endpoint);
            await command.ExecuteNonQueryAsync();
        }

        private async Task<NpgsqlConnection> OpenAsync()
        {
            var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            return connection;
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_part04_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin)
                ;
            await drop.ExecuteNonQueryAsync();
        }
    }
}
