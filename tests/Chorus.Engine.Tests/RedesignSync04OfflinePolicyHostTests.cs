using System.Globalization;
using System.Security.Cryptography;
using Chorus.Client;
using Chorus.ExampleGame;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-04 acceptance (配置进入房间持久配置): the all-offline policy the game host
/// validated at admission lands in the room row in its canonical form and rides the
/// recovery view back — and a room whose persisted declaration no longer matches what the
/// package ships is refused at recovery, never silently re-declared (authoritative-sync.md:44).
/// The admission rules and recovery pin are unit-level; this is the real-persistence round
/// trip through the data server.
/// </summary>
public sealed class RedesignSync04OfflinePolicyHostTests
{
    [Fact]
    public async Task TheRoomPersistsItsOfflinePolicyAndAChangedOneIsRefusedAtRecovery()
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
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        // The store refuses a round whose participant bindings do not exist, so the room's
        // two participants are real mounted streamers.
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-sync-04-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-sync-04-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        // A single long lease suffices: the room is deferred (Preparing, no ticking actor),
        // so no frame commits race an expiring fence.
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = Room,
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        }, deadline: DateTime.UtcNow.AddSeconds(10));
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-sync-04",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        var expected = ModuleOfflinePolicy.ProductionDefault.Canonical;
        var prepared = await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = Room, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-sync-04", DurationTicks = 1000, RandomSeed = 4242, TickMs = 25,
            DeferredActivation = true,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        });
        Assert.Equal("Prepared", prepared.Status);

        // 1. The canonical declaration landed in the room's persisted config.
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        Assert.Equal(expected, await ScalarAsync(connection,
            "SELECT offline_policy FROM game.room WHERE room_id=@room"));

        // 2. It rides the recovery view back to the recoverer, with the rest of the identity.
        using var frameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var frames = new FrameStore.FrameStoreClient(frameChannel);
        var plan = await frames.LoadRecoveryAsync(new LoadRecoveryRequest
        {
            RoomId = Room, GameId = "game-a", CandidateDepth = 2
        });
        Assert.True(plan.Found);
        Assert.Equal(expected, plan.OfflinePolicy);
        Assert.Equal("pkg-1", plan.PackageHash);

        // 3. A room whose persisted declaration no longer matches the package's current one
        // (a config upgrade that changed the policy) is refused — never silently re-declared.
        // The recovery runs on a host that holds no live actor (game-b): a host whose own
        // actor still owns the room short-circuits on the local state before the plan read,
        // so the plan-path identity check is exercised from a recoverer, not the holder.
        await ExecAsync(connection,
            "UPDATE game.room SET offline_policy=@policy WHERE room_id=@room",
            "PauseAtBoundary|15|60|2|Buffer");
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a", instanceId: "game-b");
        using var hostChannelB = environment.Channel(gamePortB, controlCertificate, "game-server");
        var reply = await new GameHost.GameHostClient(hostChannelB).RecoverRoomAsync(
            new RecoverRoomRequest
            {
                RoomId = Room, GameId = "game-a", OwnerInstanceId = "game-1",
                Epoch = owner.Epoch, TickMs = 25
            });
        Assert.False(reply.Recovered);
        Assert.Contains("offline policy", reply.Note, StringComparison.Ordinal);
        Assert.Contains("PauseAtBoundary|15|60|2|Buffer", reply.Note, StringComparison.Ordinal);
        Assert.Contains("cannot change mid-flight", reply.Note, StringComparison.Ordinal);
    }

    private const string Room = "room-sync-04";

    /// <summary>Logs a simulator streamer in through the real StreamerServer and mounts game-a.</summary>
    private static async Task<(LoginReadyReply Login, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-sync-04-" + openId,
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

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("room", Room);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, string policy)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("room", Room);
        command.Parameters.AddWithValue("policy", policy);
        await command.ExecuteNonQueryAsync();
    }
}
