using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Chorus.Rpc;
using Npgsql;
using System.Globalization;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The management-operation API end to end over the browser face (FIX-WEB-09): a room
/// command dispatched as JSON, its effect queried by the operation id through the data
/// plane to the applied tick, and a control-plane command whose id answers from the audit
/// trail — one status query serving both, so the operations page (FIX-WEB-10) submits and
/// queries without knowing which plane holds the answer. The setup is the FIX-GM-07
/// environment; everything past it rides HTTP+JSON with the operator bearer alone.
/// </summary>
public sealed class RedesignWeb09OperationsApiHostTests
{
    private const string Room = "room-web09";
    private const long Seed = 909;

    [Fact]
    public async Task TheBrowserFaceSubmitsOperationsAndQueriesTheirResultsById()
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

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO auth.streamer_state(streamer_id,tenant_id) VALUES ('s-web09-a','tenant-a');
                INSERT INTO auth.streamer_state(streamer_id,tenant_id) VALUES ('s-web09-b','tenant-a');
                INSERT INTO live.session(live_session_id,tenant_id,streamer_id,platform,app_id,
                    platform_room_key,canonical_live_session_key,started_at)
                    VALUES ('session-web09-a','tenant-a','s-web09-a','simulator','app-1','key-web09-a','simulator:key-web09-a',clock_timestamp());
                INSERT INTO live.session(live_session_id,tenant_id,streamer_id,platform,app_id,
                    platform_room_key,canonical_live_session_key,started_at)
                    VALUES ('session-web09-b','tenant-a','s-web09-b','simulator','app-1','key-web09-b','simulator:key-web09-b',clock_timestamp());
                INSERT INTO live.binding(binding_id,live_session_id,tenant_id,game_id,generation,
                    state,current_room_id,round_generation)
                    VALUES ('binding-web09-a','session-web09-a','tenant-a','game-a',1,'Ready',NULL,1);
                INSERT INTO live.binding(binding_id,live_session_id,tenant_id,game_id,generation,
                    state,current_room_id,round_generation)
                    VALUES ('binding-web09-b','session-web09-b','tenant-a','game-a',1,'Ready',NULL,1);
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-web09");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-web09"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-web09" });
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

        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-web09");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        await new RoundStore.RoundStoreClient(gameChannel).StartRoundAsync(new StartRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-a", Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-web09", TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = "binding-web09-a", ParticipantId = "s-web09-a", Role = "Host" },
                new RoundParticipantProto { BindingId = "binding-web09-b", ParticipantId = "s-web09-b", Role = "Guest" }
            }
        });
        using var hostChannel = environment.Channel(gamePort, controlCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = Room, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = claimed.Epoch,
                OperationId = "run-web09", DurationTicks = 60000, RandomSeed = Seed, TickMs = 5,
                PackageHash = "pkg-1",
                Participants =
                {
                    new GameParticipantProto { BindingId = "binding-web09-a", ParticipantId = "s-web09-a", Faction = "red" },
                    new GameParticipantProto { BindingId = "binding-web09-b", ParticipantId = "s-web09-b", Faction = "blue" }
                }
            });
        Assert.Equal("Running", prepared.Status);

        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        var baseUrl = $"https://localhost:{environment.BrowserPort}/admin/api/v1";

        // (1) The room command is submitted as JSON over the browser face: DurableAccepted
        // at the owner, the operation id echoed back, never "Applied".
        const string operationId = "web09-boost-1";
        using (var dispatch = await PostAsync(client, baseUrl + "/dispatch-room-command", "alice-token",
            $$"""
            {"requestId":"{{operationId}}","operator":"gm-alice","reason":"playtest boost",
             "tenantId":"tenant-a","gameId":"game-a","roomId":"{{Room}}",
             "expectedEpoch":{{claimed.Epoch}},"participantId":"s-web09-a",
             "payloadJson":"{\"action\":\"boost\",\"level\":1}"}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
            using var reply = JsonDocument.Parse(await dispatch.Content.ReadAsStringAsync());
            Assert.Equal(operationId, reply.RootElement.GetProperty("operationId").GetString());
            Assert.Equal("DurableAccepted", reply.RootElement.GetProperty("stage").GetString());
            Assert.Equal("game-a", reply.RootElement.GetProperty("ownerInstanceId").GetString());
        }

        // (2) The result query answers the room command from the data plane, over JSON: the
        // same operation id turns from "Accepted" to "Applied at tick N" — and the 64-bit
        // applied tick rides the wire as a JSON number, the shape the generated client's
        // numeric fields mirror (FIX-WEB-05).
        long appliedTick = 0;
        await WaitUntilAsync(async () =>
        {
            using var status = await PostAsync(client, baseUrl + "/get-operation-status", "alice-token",
                $$"""{"operationId":"{{operationId}}","tenantId":"tenant-a","gameId":"game-a","roomId":"{{Room}}"}""");
            if (status.StatusCode != HttpStatusCode.OK) return false;
            using var reply = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            if (reply.RootElement.GetProperty("stage").GetString() != "Applied") return false;
            // The 64-bit tick rides the wire as a JSON number — the shape the generated
            // client's numeric fields mirror (FIX-WEB-05).
            Assert.Equal(JsonValueKind.Number, reply.RootElement.GetProperty("appliedTick").ValueKind);
            appliedTick = reply.RootElement.GetProperty("appliedTick").GetInt64();
            return true;
        }, TimeSpan.FromSeconds(30), "The dispatched command never reached a committed frame.");
        Assert.True(appliedTick >= 1, $"the applied tick must be a real frame, saw {appliedTick}");
        using (var status = await PostAsync(client, baseUrl + "/get-operation-status", "alice-token",
            $$"""{"operationId":"{{operationId}}"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            using var reply = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            // The room scope is optional: the id alone finds the same answer.
            Assert.Equal("Applied", reply.RootElement.GetProperty("stage").GetString());
            Assert.Equal(appliedTick, reply.RootElement.GetProperty("appliedTick").GetInt64());
        }

        // (3) A control-plane command's id answers from the audit trail: the queue gate has
        // no data-plane input, so the fallback says "Audited" and hands back the recorded
        // evidence — the synchronous reply was the outcome, the row is what proves it.
        const string gateId = "web09-gate-1";
        using (var suspend = await PostAsync(client, baseUrl + "/suspend-match-queue", "alice-token",
            $$"""{"requestId":"{{gateId}}","operator":"gm-alice","reason":"incident web09","partitionKey":"test/web09"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        }
        using (var status = await PostAsync(client, baseUrl + "/get-operation-status", "alice-token",
            """{"operationId":"web09-gate-1"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            using var reply = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.True(reply.RootElement.GetProperty("found").GetBoolean());
            Assert.Equal("Audited", reply.RootElement.GetProperty("stage").GetString());
            Assert.Equal("SuspendMatchQueue", reply.RootElement.GetProperty("platform").GetString());
            Assert.Contains("test/web09", reply.RootElement.GetProperty("note").GetString());
            Assert.Equal(0, reply.RootElement.GetProperty("appliedTick").GetInt64());
        }

        // (4) An id under nothing at all is an honest NotFound — not folded into an accepted
        // answer.
        using (var unknown = await PostAsync(client, baseUrl + "/get-operation-status", "alice-token",
            """{"operationId":"web09-none"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
            using var reply = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
            Assert.False(reply.RootElement.GetProperty("found").GetBoolean());
            Assert.Equal("NotFound", reply.RootElement.GetProperty("stage").GetString());
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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url,
        string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
}
