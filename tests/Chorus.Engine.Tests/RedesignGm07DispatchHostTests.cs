using Chorus.Rpc;
using Grpc.Core;
using Npgsql;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The operator-delegated playtest command end to end (FIX-GM-07, architecture 12.1): the
/// ControlServer authenticates the operator, resolves the owner from its own directory and
/// audits before it acts; the owner host re-validates the target version, resolves the
/// participant's live binding itself and forwards the command through the same durable
/// ordered-input path a platform event rides — so it takes effect at a committed tick, the
/// frame's input refs name the operation id, and the effect query answers "accepted" and
/// "applied at tick N" as distinct stages of one operation id. A replayed request id is a
/// visible duplicate, a stale expected epoch is refused, an unknown participant is refused,
/// and a role below "gm" never reaches the dispatch.
/// </summary>
public sealed class RedesignGm07DispatchHostTests
{
    private const string Room = "room-gm07";
    private const long Seed = 777;

    [Fact]
    public async Task AnOperatorDelegatedCommandRidesTheRoomInputPathAndAnswersItsEffectQuery()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-a=https://localhost:{gamePort}",
                ["CHORUS_RECOVERY_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_STALL_SWEEP_INTERVAL_MS"] = "60000",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                // The dispatch must find the lease alive however starved the runner —
                // nothing here waits for a lapse (the direct-push chain's recipe).
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "60",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "15"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_SLOT_ID"] = "node-a/game-server",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            }, clientTenant: "tenant-a", instanceId: "game-a");

        var operators = new Chorus.ControlServer.RedesignOperatorStore(environment.DatabaseConnectionString);
        await operators.RegisterOperatorAsync("gm-alice", "gm", ["tenant-a"], ["game-a"], "alice-token");
        await operators.RegisterOperatorAsync("viewer-carl", "viewer", ["tenant-a"], ["game-a"], "carl-token");

        // Two seats for the duel's red/blue factions. The bindings start unattached and
        // Ready; StartRound's admission points them at the room and bumps their generation —
        // exactly what a mount-then-join flow produces, minus the streamer stack. The
        // canonical key stays set: the routing snapshot reads the session row as it is.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO auth.streamer_state(streamer_id,tenant_id) VALUES ('s-gm07-a','tenant-a');
                INSERT INTO auth.streamer_state(streamer_id,tenant_id) VALUES ('s-gm07-b','tenant-a');
                INSERT INTO live.session(live_session_id,tenant_id,streamer_id,platform,app_id,
                    platform_room_key,canonical_live_session_key,started_at)
                    VALUES ('session-gm07-a','tenant-a','s-gm07-a','simulator','app-1','key-gm07-a','simulator:key-gm07-a',clock_timestamp());
                INSERT INTO live.session(live_session_id,tenant_id,streamer_id,platform,app_id,
                    platform_room_key,canonical_live_session_key,started_at)
                    VALUES ('session-gm07-b','tenant-a','s-gm07-b','simulator','app-1','key-gm07-b','simulator:key-gm07-b',clock_timestamp());
                INSERT INTO live.binding(binding_id,live_session_id,tenant_id,game_id,generation,
                    state,current_room_id,round_generation)
                    VALUES ('binding-gm07-a','session-gm07-a','tenant-a','game-a',1,'Ready',NULL,1);
                INSERT INTO live.binding(binding_id,live_session_id,tenant_id,game_id,generation,
                    state,current_room_id,round_generation)
                    VALUES ('binding-gm07-b','session-gm07-b','tenant-a','game-a',1,'Ready',NULL,1);
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-gm07");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-gm07"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-gm07" });
        }
        await WaitUntilAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance =>
                instance.ProcessInstanceId == "game-a" && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30), "The game host did not self-register into the routing snapshot.");

        var control = new ControlService.ControlServiceClient(controlChannel);
        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = Room,
            OwnerInstanceId = "game-a", LeaseSeconds = 30
        });
        Assert.True(claimed.Found);
        Assert.Equal("game-a", claimed.OwnerInstanceId);

        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-gm07");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        await new RoundStore.RoundStoreClient(gameChannel).StartRoundAsync(new StartRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-a", Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-gm07", TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = "binding-gm07-a", ParticipantId = "s-gm07-a", Role = "Host" },
                new RoundParticipantProto { BindingId = "binding-gm07-b", ParticipantId = "s-gm07-b", Role = "Guest" }
            }
        });
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = Room, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = claimed.Epoch,
                OperationId = "run-gm07", DurationTicks = 60000, RandomSeed = Seed, TickMs = 5,
                PackageHash = "pkg-1",
                Participants =
                {
                    new GameParticipantProto { BindingId = "binding-gm07-a", ParticipantId = "s-gm07-a", Faction = "red" },
                    new GameParticipantProto { BindingId = "binding-gm07-b", ParticipantId = "s-gm07-b", Faction = "blue" }
                }
            });
        Assert.Equal("Running", prepared.Status);

        var admin = new Admin.AdminClient(controlChannel);
        var gm = new GmOperatorClient(admin, "gm-alice", "alice-token");

        // (1) The dispatch rides the durable ordered-input path: DurableAccepted at the owner,
        // never "Applied" — application is the effect query's answer, not the dispatch's.
        const string operationId = "gm07-boost-1";
        var boost = await gm.DispatchRoomCommandAsync("tenant-a", "game-a", Room, operationId,
            "playtest boost", "s-gm07-a", """{"action":"boost","level":1}""", expectedEpoch: claimed.Epoch);
        Assert.Equal(operationId, boost.OperationId);
        Assert.Equal("DurableAccepted", boost.Stage);
        Assert.False(boost.Duplicate);
        Assert.Equal("game-a", boost.OwnerInstanceId);
        Assert.Equal(claimed.Epoch, boost.Epoch);

        // (2) The room consumes the command at a committed tick: the effect query turns from
        // "Accepted" to "Applied at tick N" — accepted and applied are distinct answers.
        var applied = await WaitForAsync(async () =>
        {
            var status = await gm.GetOperationStatusAsync(operationId, "tenant-a", "game-a", Room);
            return status.Stage == "Applied" ? status : null;
        }, TimeSpan.FromSeconds(30));
        Assert.NotNull(applied);
        Assert.True(applied!.AppliedTick >= 1, $"the applied tick must be a real frame, saw {applied.AppliedTick}");
        Assert.Equal("gm", applied.Platform);

        // (3) The data plane agrees twice over: the frame's input refs name the operation id,
        // and the module's score effect proves the command took effect — a level-1 boost adds
        // 10×1 + seed%5 points to red, where the seed is the module's per-tick LCG state (the
        // product overflows past the first tick, so the residue is not a constant and the
        // consuming tick moves with scheduler timing). The stable facts are the shape: red
        // scored between 10 and 14 on a fresh room.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var frame = new NpgsqlCommand("""
                SELECT input_refs::text,effects::text FROM game.frame
                WHERE room_id=@room AND tick_id=@tick
                """, connection);
            frame.Parameters.AddWithValue("room", Room);
            frame.Parameters.AddWithValue("tick", applied.AppliedTick);
            await using var reader = await frame.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "no committed frame at the applied tick");
            Assert.Contains(operationId, reader.GetString(0));
            var effects = reader.GetString(1);
            var delta = Regex.Match(effects, "\\\\\"delta\\\\\":(\\d+)");
            Assert.True(delta.Success, $"the score effect carries no delta: {effects}");
            Assert.InRange(long.Parse(delta.Groups[1].Value, CultureInfo.InvariantCulture), 10, 14);
            Assert.Contains("\\\"faction\\\":\\\"red\\\"", effects);
        }

        // (4) A replayed request id is a visible duplicate of the durable input — the same
        // operation id never produces a second effect, and the stage answers what stands.
        var replay = await gm.DispatchRoomCommandAsync("tenant-a", "game-a", Room, operationId,
            "playtest boost", "s-gm07-a", """{"action":"boost","level":1}""", expectedEpoch: claimed.Epoch);
        Assert.True(replay.Duplicate);
        Assert.Contains(replay.Stage, (string[])["Accepted", "Applied"]);
        var after = await gm.GetOperationStatusAsync(operationId, "tenant-a", "game-a", Room);
        Assert.Equal("Applied", after.Stage);
        Assert.Equal(applied.AppliedTick, after.AppliedTick);
        Assert.True(await FrameCountAsync(environment, operationId) == 1,
            "the replayed operation id must not appear in a second frame");

        // (5) A stale expected epoch is refused before anything moves.
        var stale = await Assert.ThrowsAsync<RpcException>(() => gm.DispatchRoomCommandAsync(
            "tenant-a", "game-a", Room, "gm07-stale", "stale probe", "s-gm07-a",
            """{"action":"boost","level":1}""", expectedEpoch: claimed.Epoch + 5));
        Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);

        // (6) A participant without a live seat is refused — no seat is invented.
        var unknown = await gm.DispatchRoomCommandAsync("tenant-a", "game-a", Room, "gm07-unknown",
            "unknown seat probe", "s-gm07-nobody", """{"action":"boost","level":1}""");
        Assert.Equal("UnknownParticipant", unknown.Stage);

        // (7) A role below "gm" never reaches the dispatch.
        var lowRole = await Assert.ThrowsAsync<RpcException>(() => new Admin.AdminClient(controlChannel)
            .DispatchRoomCommandAsync(new AdminDispatchRoomCommandRequest
            {
                TenantId = "tenant-a", GameId = "game-a", RoomId = Room, RequestId = "gm07-low",
                Reason = "low role", Operator = "viewer-carl", ParticipantId = "s-gm07-a",
                PayloadJson = """{"action":"boost","level":1}"""
            }, new Metadata { { "authorization", "Bearer carl-token" } }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, lowRole.StatusCode);

        // (8) The audit row stands before the effect: operator, reason, target and evidence.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var audit = new NpgsqlCommand("""
                SELECT operator,reason,command FROM control.audit WHERE request_id=@op
                """, connection);
            audit.Parameters.AddWithValue("op", operationId);
            await using var reader = await audit.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "the dispatch wrote no audit row");
            Assert.Equal("gm-alice", reader.GetString(0));
            Assert.Equal("playtest boost", reader.GetString(1));
            Assert.Equal("DispatchRoomCommand", reader.GetString(2));
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe, TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return;
            await Task.Delay(100);
        }
        Assert.True(false, failureMessage);
    }

    private static async Task<long> FrameCountAsync(RedesignM0HostTests.HostEnvironment environment,
        string operationId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("""
            SELECT count(*) FROM game.frame WHERE room_id=@room AND input_refs @> to_jsonb(ARRAY[@op]::text[])
            """, connection);
        count.Parameters.AddWithValue("room", Room);
        count.Parameters.AddWithValue("op", operationId);
        return Convert.ToInt64(await count.ExecuteScalarAsync());
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe, TimeSpan timeout)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = await probe();
            if (found is not null) return found;
            await Task.Delay(100);
        }
        return null;
    }
}
