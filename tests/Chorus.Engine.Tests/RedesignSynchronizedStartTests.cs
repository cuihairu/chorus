using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-PERF-03, the host leg: rooms prepared with deferred activation sit still — round
/// Preparing, no frames — until the control plane releases the whole fleet across one start
/// line, and the first frame of every room then commits inside a tight common window. The
/// release stays fenced and idempotent over the wire.
/// </summary>
public sealed class RedesignSynchronizedStartTests
{
    private const int RoomCount = 12;

    private readonly ITestOutputHelper _output;

    public RedesignSynchronizedStartTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task HeldRoomsSitStillUntilTheFleetIsReleasedTogether()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        // One mount per room: a binding names one live room (live.binding.current_room_id is
        // single-valued), so a fleet needs a streamer per room — same shape as the benchmark.
        var rooms = new List<(string StreamerId, string BindingId)>();
        for (var i = 0; i < RoomCount; i++)
        {
            var (login, binding) = await MountAsync(environment, simulator, $"open-sync-{i}", $"live-sync-{i}");
            rooms.Add((login.StreamerId, binding));
        }

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);

        // Admit the fleet, then hand every room to the game host with deferred activation:
        // each room is prepared and held, none is running yet.
        var prepareTasks = new Task<PrepareRoomReply>[RoomCount];
        var epochs = new long[RoomCount];
        for (var i = 0; i < RoomCount; i++)
        {
            var roomId = $"room-sync-{i}";
            var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
                OwnerInstanceId = "game-1", LeaseSeconds = 30
            });
            epochs[i] = owner.Epoch;
            var admission = await rounds.StartRoundAsync(new StartRoundRequest
            {
                RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
                ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
                OperationId = $"start-op-{i}",
                Participants =
                {
                    new RoundParticipantProto { BindingId = rooms[i].BindingId, ParticipantId = rooms[i].StreamerId, Role = "Host" }
                }
            });
            Assert.False(admission.Duplicate);
            prepareTasks[i] = gameHost.PrepareRoomAsync(new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
                OperationId = $"run-op-{i}", DurationTicks = 60, RandomSeed = 12345 + i, TickMs = 50,
                DeferredActivation = true,
                Participants =
                {
                    new GameParticipantProto { BindingId = rooms[i].BindingId, ParticipantId = rooms[i].StreamerId, Faction = "red" }
                }
            }).ResponseAsync;
        }
        foreach (var prepared in await Task.WhenAll(prepareTasks))
        {
            Assert.False(prepared.Duplicate);
            Assert.Equal("Prepared", prepared.Status);
        }

        // Held means held: every round still Preparing on the data plane, and long past
        // several tick cadences no room has committed a single frame.
        await Task.Delay(600);
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using (var statuses = new NpgsqlCommand(
                "SELECT count(*) FILTER (WHERE status='Preparing'), count(*) FROM game.room WHERE room_id LIKE 'room-sync-%'",
                connection))
            await using (var reader = await statuses.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal((long)RoomCount, reader.GetInt64(0));
                Assert.Equal((long)RoomCount, reader.GetInt64(1));
            }
            await using (var frames = new NpgsqlCommand(
                "SELECT count(*) FROM game.frame WHERE room_id LIKE 'room-sync-%'", connection))
                Assert.Equal(0L, Convert.ToInt64(await frames.ExecuteScalarAsync()));
        }

        // A stale fence never releases a room; the fleet stays at zero frames.
        var stale = await Assert.ThrowsAsync<RpcException>(() => gameHost.ActivateRoomAsync(
            new ActivateRoomRequest
            {
                RoomId = "room-sync-0", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epochs[0] + 99,
                OperationId = "release-stale"
            }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, stale.StatusCode);

        // The start line: the whole fleet is released in one burst.
        var line = DateTime.UtcNow;
        var releases = await Task.WhenAll(Enumerable.Range(0, RoomCount).Select(i =>
            gameHost.ActivateRoomAsync(new ActivateRoomRequest
            {
                RoomId = $"room-sync-{i}", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epochs[i],
                OperationId = $"release-{i}"
            }).ResponseAsync));
        Assert.All(releases, release =>
        {
            Assert.False(release.Duplicate);
            Assert.Equal("Running", release.Status);
        });
        var releaseBurstMs = (DateTime.UtcNow - line).TotalMilliseconds;

        // A retry after a lost reply answers duplicate over the wire and never double-starts.
        var replay = await gameHost.ActivateRoomAsync(new ActivateRoomRequest
        {
            RoomId = "room-sync-0", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epochs[0],
            OperationId = "release-0-retry"
        });
        Assert.True(replay.Duplicate);

        // First frame of every room: each room's first frame.commit receipt lands inside a
        // tight common window measured on the data plane's commit clock.
        var firsts = new List<DateTime>();
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (firsts.Count < RoomCount && !deadline.IsCompleted)
        {
            await Task.Delay(100);
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("""
                SELECT min(committed_at) FROM infra.operation_receipt
                WHERE operation_kind='frame.commit' AND tenant_id='tenant-a'
                  AND target_id LIKE 'room-sync-%'
                GROUP BY target_id
                """, connection);
            await using var reader = await query.ExecuteReaderAsync();
            firsts.Clear();
            while (await reader.ReadAsync()) firsts.Add(reader.GetDateTime(0));
        }
        Assert.True(firsts.Count == RoomCount,
            $"only {firsts.Count} of {RoomCount} rooms committed a first frame");
        var spread = (firsts.Max() - firsts.Min()).TotalMilliseconds;
        _output.WriteLine($"release burst {releaseBurstMs:F0} ms; first-frame spread {spread:F0} ms");

        // One cadence of head start from the release burst, plus the burst itself: the fleet's
        // first frames must land together, not strung out over seconds of staggered starts.
        Assert.True(spread <= releaseBurstMs + 150,
            $"first frames strung out {spread:F0} ms after a {releaseBurstMs:F0} ms release burst");

        // The fleet runs to its results: every room completes its 60 ticks.
        var finished = false;
        deadline = Task.Delay(TimeSpan.FromSeconds(60));
        while (!deadline.IsCompleted)
        {
            await Task.Delay(250);
            await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand(
                "SELECT count(DISTINCT room_id), max(tick_id) FROM game.frame WHERE room_id LIKE 'room-sync-%'",
                connection);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            if (reader.GetInt64(0) == RoomCount && reader.GetInt64(1) >= 60)
            {
                finished = true;
                break;
            }
        }
        Assert.True(finished, "the fleet did not run to 60 ticks in every room");
    }

    private static async Task<(LoginReadyReply Login, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId, string liveKey)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server",
            "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login, mounted.BindingId);
    }
}
