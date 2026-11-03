using System.Globalization;
using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-06 acceptance (重启仍保持暂停): the pause is a durable store fact, never a
/// stopped local timer — PauseRound flips the round Running to Paused under the ownership
/// fence, records the frozen head (last_tick, state_version) and the wall-clock pause
/// instant, and a recovery load carries the room back as Paused so a restart restores the
/// freeze instead of ticking past it. A paused room accepts no further lifecycle flip except
/// a resume (SYNC-08): re-pause, activation and suspension are all refused. The mechanism
/// (a PauseAtBoundary actor parking itself) is unit-level in
/// <see cref="RedesignSync06PauseUnitTests"/>; this is the real-persistence round trip.
/// </summary>
public sealed class RedesignSync06PauseHostTests
{
    [Fact]
    public async Task APausedRoomPersistsItsFreezeAndRecoveryCarriesItBackAsPaused()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-sync-06-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-sync-06-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var frames = new FrameStore.FrameStoreClient(gameChannel);
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = Room,
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        }, deadline: DateTime.UtcNow.AddSeconds(10));

        // Start → Preparing, deposit the tick-0 snapshot → Preparing, activate → Running
        // at head tick 0 / state version 1 (no actor ticks in this store-level round trip).
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-sync-06",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        await rounds.PrepareRoundAsync(new PrepareRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            OperationId = "prepare-sync-06", TickMs = 25, RecoveryMode = "DeterministicReplay",
            OfflinePolicy = "ContinueToEnd|30|120|1|Buffer",
            SnapshotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(64)),
            SnapshotHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            SnapshotSchemaVersion = 1
        });
        var activated = await rounds.ActivateRoundAsync(new ActivateRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            OperationId = "activate-sync-06"
        });
        Assert.Equal("Running", activated.Status);

        // The pause parks the room at the committed boundary (tick 0 / v1) it froze at.
        var paused = await rounds.PauseRoundAsync(new PauseRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            Reason = "all control sessions offline past the declared grace",
            OperationId = "pause-sync-06", Tick = 0, StateVersion = 1
        });
        Assert.Equal("Paused", paused.Status);

        // The freeze is a durable row fact: status Paused, the wall-clock pause instant
        // recorded, the head frozen exactly where the room stopped.
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using (var row = new NpgsqlCommand(
            "SELECT status,last_tick,state_version,paused_at,paused_reason FROM game.room WHERE room_id=@room",
            connection))
        {
            row.Parameters.AddWithValue("room", Room);
            await using var reader = await row.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "The room row vanished.");
            Assert.Equal("Paused", reader.GetString(0));
            Assert.Equal(0, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
            Assert.False(reader.IsDBNull(3), "paused_at was not recorded.");
            Assert.Contains("all control sessions offline", reader.GetString(4), StringComparison.Ordinal);
        }

        // A recovery load carries the room back as Paused with the frozen head — the
        // restart-keeps-paused proof: a host that reloads this room sees Paused, not Running,
        // and will not tick past the boundary.
        var plan = await frames.LoadRecoveryAsync(new LoadRecoveryRequest
        {
            RoomId = Room, GameId = "game-a", CandidateDepth = 2
        });
        Assert.True(plan.Found);
        Assert.Equal("Paused", plan.Status);
        Assert.Equal(0, plan.LastTick);
        Assert.Equal(1, plan.StateVersion);

        // A paused room accepts no further lifecycle flip except a resume: re-pause,
        // activation and suspension are all refused — the freeze holds.
        var repause = await Assert.ThrowsAsync<RpcException>(() => rounds.PauseRoundAsync(
            new PauseRoundRequest
            {
                RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
                Reason = "again", OperationId = "pause-sync-06-b", Tick = 0, StateVersion = 1
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, repause.StatusCode);
        var reactivate = await Assert.ThrowsAsync<RpcException>(() => rounds.ActivateRoundAsync(
            new ActivateRoundRequest
            {
                RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
                OperationId = "activate-sync-06-b"
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, reactivate.StatusCode);
        var suspend = await Assert.ThrowsAsync<RpcException>(() => rounds.SuspendRoundAsync(
            new SuspendRoundRequest
            {
                RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
                Reason = "damaged", OperationId = "suspend-sync-06"
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, suspend.StatusCode);
    }

    [Fact]
    public async Task APauseNamedAtTheWrongBoundaryIsRefused()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-sync-06-c");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-sync-06-d");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = Room,
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        }, deadline: DateTime.UtcNow.AddSeconds(10));
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-sync-06-b",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        await rounds.PrepareRoundAsync(new PrepareRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            OperationId = "prepare-sync-06-b", TickMs = 25, RecoveryMode = "DeterministicReplay",
            OfflinePolicy = "ContinueToEnd|30|120|1|Buffer",
            SnapshotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(64)),
            SnapshotHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            SnapshotSchemaVersion = 1
        });
        await rounds.ActivateRoundAsync(new ActivateRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            OperationId = "activate-sync-06-b"
        });

        // The head is tick 0 / v1; naming tick 5 / v6 is a wrong freeze point and is
        // refused — the pause can only park the room exactly where it stopped.
        var wrong = await Assert.ThrowsAsync<RpcException>(() => rounds.PauseRoundAsync(
            new PauseRoundRequest
            {
                RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
                Reason = "offline", OperationId = "pause-sync-06-b", Tick = 5, StateVersion = 6
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, wrong.StatusCode);
        Assert.Contains("not the room's committed head", wrong.Status.Detail, StringComparison.Ordinal);
    }

    private const string Room = "room-sync-06";

    private static async Task<(LoginReadyReply Login, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-sync-06-" + openId,
            TimeSpan.FromMinutes(10));
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
        return (login, mounted.BindingId);
    }
}
