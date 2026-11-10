using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The release block end-to-end (FIX-SETTLE-05): a finishing room whose reward manifest is
/// never settled stands before the data plane's finalize evidence gate, and the coordinator
/// parks the release with the refusal's reason queryable on the room snapshot — the room is
/// not forced Settled. The admin repair completes the grants; the coordinator's next pass
/// releases the room on its own, records the dwell and clears the reason. The end ladder's
/// tail is fabricated at the release phase (the ladder itself is END-01..14's domain); the
/// release step this fact exercises is exactly the evidence gate the block records.
/// </summary>
public sealed class RedesignSettleBlockHostTests
{
    [Fact]
    public async Task AReleaseBlockedOnEvidenceIsQueryableAndContinuesWhenItClears()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        // The executor is off, so the manifest stays Planned and the release blocks on it
        // deterministically; the admin repair is the only driver that completes the grants.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_SETTLE_EXECUTOR_INTERVAL_MS"] = "0",
                ["CHORUS_END_COORDINATOR_INTERVAL_MS"] = "150",
                ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600"
            });

        const string room = "room-st5-a";
        const string operation = "end-st5-a";
        const string owner = "game-st5-a";
        await FinishRoundAsync(environment, simulator, room, "live-st5-a", "st5-a-host", owner,
            new PlannedGrantProto
            {
                PlayerId = "player-st5", GrantKey = "round-score", OperationId = room + "/grant/1",
                AssetId = "points", Delta = 100
            },
            new PlannedGrantProto
            {
                PlayerId = "player-st5", GrantKey = "mvp", OperationId = room + "/grant/2",
                AssetId = "points", Delta = 30
            });

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-st5");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        using var dataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");

        // The ladder's tail stands at the release phase; the coordinator walks it and parks
        // on the evidence gate, recording the block.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO control.end_operation(tenant_id,game_id,end_operation_id,room_id,
                    round_generation,binding_generations,final_tick,result_version,phase)
                VALUES ('tenant-a','game-a',@operation,@room,1,'[]'::jsonb,1,1,'PlatformClosed')
                """, connection);
            insert.Parameters.AddWithValue("operation", operation);
            insert.Parameters.AddWithValue("room", room);
            await insert.ExecuteNonQueryAsync();
        }

        // The data plane refuses the release while the manifest is incomplete — a caller
        // cannot force Settled, no matter which authorized role asks.
        var rejected = await Assert.ThrowsAsync<RpcException>(async () =>
            await new Settlement.SettlementClient(dataChannel).FinalizeRoundAsync(
                new FinalizeRoundRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", RoomId = room, OperationId = "proof-" + room
                }));
        Assert.Equal(StatusCode.FailedPrecondition, rejected.StatusCode);
        Assert.Contains("manifest is incomplete", rejected.Status.Detail);

        // The block is queryable: the reason, the wait's start, and the room not settled.
        var blocked = await PollUntilAsync(environment, room, blocked: true);
        Assert.True(blocked, "The coordinator never recorded the release block.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal("Finishing", await ScalarAsync(connection,
                "SELECT status FROM game.room WHERE room_id=@room", room));
            Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection,
                "SELECT count(*) FROM settlement.grant WHERE room_id=@room AND status='Granted'", room)));
        }

        // The condition clears: the admin repair completes the grants, and the coordinator's
        // next pass releases the room without any further human step.
        // FIX-WEB-03: the retry command now requires an operator identity, not a process
        // certificate — the test-st5 credential the command body names drives the call.
        await new RedesignOperatorStore(environment.DatabaseConnectionString)
            .RegisterOperatorAsync("test-st5", "gm", [], [], "st5-token");
        await new Admin.AdminClient(
            controlChannel.Intercept(new OperatorCredentialInterceptor("st5-token")))
            .RetrySettlementAsync(new AdminSettlementRetryRequest
            {
                RequestId = "retry-st5-a", Operator = "test-st5", Reason = "complete the manifest",
                RoomId = room
            });

        var settled = await PollUntilAsync(environment, room, blocked: false);
        Assert.True(settled, "The room never released after the block cleared.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal("Settled", await ScalarAsync(connection,
                "SELECT status FROM game.room WHERE room_id=@room", room));
            var dwell = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT blocked_total_ms FROM control.end_operation WHERE end_operation_id=@operation",
                "operation", operation));
            Assert.True(dwell > 0, "The dwell was never recorded.");
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(connection,
                "SELECT count(DISTINCT operation_id) FROM player.ledger WHERE reason LIKE 'settlement:" + room + "/%'",
                room)));
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(connection,
                "SELECT count(*) FROM player.ledger WHERE reason LIKE 'settlement:" + room + "/%'", room)));
        }
    }

    /// <summary>Polls the room snapshot until the block reason is set (blocked) or cleared
    /// with a recorded dwell (released).</summary>
    private static async Task<bool> PollUntilAsync(RedesignM0HostTests.HostEnvironment environment,
        string room, bool blocked)
    {
        using var certificate = environment.Certificate("@cluster", "control-server", "control-st5-poll");
        using var channel = environment.Channel(environment.ControlPort, certificate, "control-server");
        // FIX-WEB-03: the room snapshot is a viewer read — the operator credential drives the
        // poll (the operator is idempotently registered on the ON CONFLICT path).
        await new RedesignOperatorStore(environment.DatabaseConnectionString)
            .RegisterOperatorAsync("test-st5", "gm", [], [], "st5-token");
        var admin = new Admin.AdminClient(
            channel.Intercept(new OperatorCredentialInterceptor("st5-token")));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var reply = await admin.GetRoomSnapshotAsync(new AdminRoomSnapshotRequest { RoomId = room });
            if (blocked)
            {
                if (reply.ReleaseBlockedReason.Length > 0 && reply.ReleaseBlockedSinceUnixMs > 0)
                    return true;
            }
            else if (reply.ReleaseBlockedReason.Length == 0 && reply.ReleaseBlockedTotalMs > 0)
            {
                return true;
            }
            await Task.Delay(150);
        }
        return false;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, string parameter)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("room", parameter);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, string name,
        string parameter)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(name, parameter);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Admits a room and commits a final frame carrying the given grants — the room
    /// stands Finishing with the manifest Planned and no game host or executor to settle it.</summary>
    private static async Task FinishRoundAsync(RedesignM0HostTests.HostEnvironment environment,
        DevelopmentPlatformSimulator simulator, string room, string liveKey, string openId,
        string gameInstance, params PlannedGrantProto[] grants)
    {
        var (streamerId, bindingId) = await MountAsync(environment, simulator, openId, liveKey);
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-" + openId);
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        var owner = await new ControlService.ControlServiceClient(controlChannel).ClaimOwnershipAsync(
            new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = room,
                OwnerInstanceId = gameInstance, LeaseSeconds = 60
            });
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", gameInstance);
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        await new RoundStore.RoundStoreClient(gameChannel).StartRoundAsync(new StartRoundRequest
        {
            RoomId = room, OwnerInstanceId = gameInstance, Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-" + openId,
            Participants = { new RoundParticipantProto
            {
                BindingId = bindingId, ParticipantId = streamerId, Role = "Host"
            } }
        });
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var activate = new NpgsqlCommand(
                "UPDATE game.room SET status='Running' WHERE room_id=@room", connection);
            activate.Parameters.AddWithValue("room", room);
            await activate.ExecuteNonQueryAsync();
        }
        var frames = new FrameStore.FrameStoreClient(gameChannel);
        var final = new CommitFrameRequest
        {
            RoomId = room, GameId = "game-a", OwnerInstanceId = gameInstance, Epoch = owner.Epoch,
            CommitId = "final-" + openId, PrevTick = 0, PrevStateVersion = 0,
            StateHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(8)),
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1",
            Final = true, ResultJson = """{"ranking":[{"faction":"red","score":130}]}""",
            PlanHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(8))
        };
        foreach (var grant in grants)
            final.Grants.Add(grant);
        await frames.CommitFrameAsync(final);
    }

    private static async Task<(string StreamerId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId, string liveKey)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login.StreamerId, mounted.BindingId);
    }
}
