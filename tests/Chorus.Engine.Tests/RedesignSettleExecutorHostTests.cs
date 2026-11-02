using System.Security.Cryptography;
using Chorus.Platform;
using Google.Protobuf;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The settlement executor's claim and retry discipline over real hosts (FIX-SETTLE-03):
/// two control-plane executors sweeping one half-open manifest settle it with exactly one
/// ledger effect per grant — the claim lease serialises them, and the per-grant receipts
/// floor any overlap — and a manifest the store refuses is recorded as a queryable
/// Retryable task that escalates to NeedsAttention at the attempt budget, never blocking
/// the sibling rooms the same pass walks. The completion is the executor's hand alone: no
/// game host runs, so the final frame and manifest are committed by the test and only the
/// business executor drives the settlement.
/// </summary>
public sealed class RedesignSettleExecutorHostTests
{
    [Fact]
    public async Task TwoConcurrentExecutorsSettleOneManifestIntoOneEffectPerGrant()
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

        // Two concurrent business executors, both sweeping on a fast cadence. The claim
        // lease hands the room to exactly one of them; the other's claim is refused.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            ExecutorEnvironment(environment, intervalMs: 400), instanceId: "control-a");
        await environment.StartAsync("Chorus.ControlServer", RedesignM0HostTests.HostEnvironment.FreePort(),
            "control-server", ExecutorEnvironment(environment, intervalMs: 400), instanceId: "control-b");

        const string room = "room-st3-a";
        await FinishRoundAsync(environment, simulator, room, "live-st3-a", "st3-a-host", "game-st3-a",
            new PlannedGrantProto
            {
                PlayerId = "player-a", GrantKey = "round-score", OperationId = room + "/grant/1",
                AssetId = "points", Delta = 100
            },
            new PlannedGrantProto
            {
                PlayerId = "player-b", GrantKey = "round-score", OperationId = room + "/grant/2",
                AssetId = "points", Delta = 30
            });

        // The manifest completes and the task leaves the table — the executor's claim and
        // completion ran, and the room is not left with a stuck task. The completion is a
        // separate round trip after the grants' receipts, so the row clears a moment after
        // the last ledger line lands — poll for it, don't assert it instantly.
        var settled = await PollUntilAsync(() => CountLedgerLinesAsync(environment, room), expected: 2);
        Assert.True(settled, "The two executors never completed the manifest.");
        var cleaned = await PollUntilExactAsync(() => SettleTaskCountAsync(environment, room), 0);
        Assert.True(cleaned, "The completed task never left the table.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var grants = new NpgsqlCommand(
                "SELECT count(*) FROM settlement.grant WHERE room_id=@room AND status='Granted'", connection);
            grants.Parameters.AddWithValue("room", room);
            Assert.Equal(2L, Convert.ToInt64(await grants.ExecuteScalarAsync()));
        }

        // Exactly one ledger line per grant — the two executors are one effect between them.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var ledger = new NpgsqlCommand(
                "SELECT count(DISTINCT operation_id),count(*) FROM player.ledger " +
                "WHERE reason LIKE 'settlement:" + room + "/%'", connection);
            await using var reader = await ledger.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
        }
    }

    [Fact]
    public async Task ARejectedManifestIsQueryableAndDoesNotBlockItsSibling()
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
        // A tight attempt budget: attempt 1 records a Retryable reason, attempt 2 escalates
        // to NeedsAttention — bounded retry, not a forever loop.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            ExecutorEnvironment(environment, intervalMs: 400, maxAttempts: 2), instanceId: "control-a");

        // Two sibling rooms in one domain, walked in one pass: the healthy room settles and
        // the rejected room records a queryable failure — one never blocks the other.
        const string healthy = "room-st3-x";
        const string rejected = "room-st3-y";
        await FinishRoundAsync(environment, simulator, healthy, "live-st3-x", "st3-x-host", "game-st3-x",
            new PlannedGrantProto
            {
                PlayerId = "player-x", GrantKey = "round-score", OperationId = healthy + "/grant/1",
                AssetId = "points", Delta = 100
            },
            new PlannedGrantProto
            {
                PlayerId = "player-x", GrantKey = "mvp", OperationId = healthy + "/grant/2",
                AssetId = "points", Delta = 30
            });
        await FinishRoundAsync(environment, simulator, rejected, "live-st3-y", "st3-y-host", "game-st3-y",
            new PlannedGrantProto
            {
                PlayerId = "player-y", GrantKey = "round-score", OperationId = rejected + "/grant/1",
                AssetId = "points", Delta = -50
            });

        // The rejected room escalates to NeedsAttention with the reason on the row.
        var escalated = await PollUntilAsync(() => SettleStatusAsync(environment, rejected),
            expected: "NeedsAttention");
        Assert.True(escalated, "The rejected manifest never escalated to NeedsAttention.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var task = new NpgsqlCommand(
                "SELECT status,attempt_count,last_error,next_attempt FROM control.settle_task " +
                "WHERE room_id=@room", connection);
            task.Parameters.AddWithValue("room", rejected);
            await using var reader = await task.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("NeedsAttention", reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.Contains("negative", reader.GetString(2));
            Assert.True(reader.IsDBNull(3), "A terminal task carries no next attempt.");
        }

        // The sibling settled regardless: two grants applied, its task completed and gone.
        var siblingSettled = await PollUntilAsync(() => CountLedgerLinesAsync(environment, healthy),
            expected: 2);
        Assert.True(siblingSettled, "The rejected room blocked its sibling.");
        var siblingCleaned = await PollUntilExactAsync(() => SettleTaskCountAsync(environment, healthy), 0);
        Assert.True(siblingCleaned, "The healthy room's task never left the table.");
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var ledger = new NpgsqlCommand(
                "SELECT count(*) FROM player.ledger WHERE reason LIKE 'settlement:" + healthy + "/%'",
                connection);
            Assert.Equal(2L, Convert.ToInt64(await ledger.ExecuteScalarAsync()));
            // The rejected grant never applied: player-y has no points and no ledger line.
            await using var rejectedLedger = new NpgsqlCommand(
                "SELECT count(*) FROM player.ledger WHERE reason LIKE 'settlement:" + rejected + "/%'",
                connection);
            Assert.Equal(0L, Convert.ToInt64(await rejectedLedger.ExecuteScalarAsync()));
        }
    }

    /// <summary>The control plane's settlement settings shared by every fact: the executor on
    /// a fast cadence, the end-operation discovery net parked far out (these facts drive the
    /// settlement, not the end ladder), and the claim lease long enough to never lapse under
    /// a healthy settle.</summary>
    private static Dictionary<string, string> ExecutorEnvironment(
        RedesignM0HostTests.HostEnvironment environment, int intervalMs, int maxAttempts = 5) =>
        new()
        {
            ["CHORUS_SETTLE_EXECUTOR_INTERVAL_MS"] = intervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_SETTLE_MAX_ATTEMPTS"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_SETTLE_LEASE_SECONDS"] = "60",
            ["CHORUS_END_DISCOVERY_GRACE_SECONDS"] = "600",
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
        };

    /// <summary>Admits a room and commits a final frame carrying the given grants — the room
    /// stands Finishing with the manifest Planned and no game host to settle it, so the
    /// business executor is the only driver.</summary>
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
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = room, OwnerInstanceId = gameInstance, Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-" + openId,
            Participants = { new RoundParticipantProto
            {
                BindingId = bindingId, ParticipantId = streamerId, Role = "Host"
            } }
        });
        // The admission chain in the host facts skips the game host's own prepare/activate —
        // the room is set Running directly so the test's final frame is the room's one frame.
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

    private static async Task<long> CountLedgerLinesAsync(RedesignM0HostTests.HostEnvironment environment,
        string room)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM player.ledger WHERE reason LIKE 'settlement:" + room + "/%'", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> SettleStatusAsync(RedesignM0HostTests.HostEnvironment environment,
        string room)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM control.settle_task WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", room);
        return await command.ExecuteScalarAsync() as string ?? "";
    }

    private static async Task<long> SettleTaskCountAsync(RedesignM0HostTests.HostEnvironment environment,
        string room)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM control.settle_task WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", room);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> PollUntilAsync(Func<Task<long>> read, long expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await read() >= expected) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<bool> PollUntilAsync(Func<Task<string>> read, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await read() == expected) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<bool> PollUntilExactAsync(Func<Task<long>> read, long expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await read() == expected) return true;
            await Task.Delay(100);
        }
        return false;
    }
}
