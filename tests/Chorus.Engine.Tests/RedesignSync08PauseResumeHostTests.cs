using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.DataServer;
using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-08 acceptance (恢复边界宿主腿): a real game host lifts a paused room back to
/// Running at its frozen head. The room is brought up paused through the data plane (the
/// shipped package's own initial state is the Prepare snapshot — the only payload a real
/// host can replay), the control participant's current session reports ready against the
/// shipped Ready=1 declaration, and the host's ResumeRoom flips the room to Running and
/// adopts exactly one actor from the verified head — the actor is real, it ticks past the
/// frozen boundary. A repeated resume answers the already-running actor (actor_running),
/// never a second tick loop. The coordinator's own leg (discovery + owner resolve + RPC) is
/// the thin driver the store tests cover; this is the host-side recovery the coordinator
/// drives.
/// </summary>
public sealed class RedesignSync08PauseResumeHostTests
{
    /// <summary>The shipped package's canonical offline policy (FIX-SYNC-04): a recovery
    /// validates the room's declaration against exactly this, so the host test must use it
    /// verbatim — Ready=1, the production threshold.</summary>
    private const string OfflinePolicy = "ContinueToEnd|30|120|1|Buffer";

    private const string OwnerInstance = "game-sync08-resume";
    private const string Room = "room-sync08-resume";

    [Fact]
    public async Task AReadyCurrentSessionResumesThePausedRoomIntoOneRunningActor()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] =
                    environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a", instanceId: OwnerInstance);

        // The data-plane bring-up (no host actor runs the round): the room is mounted, opened
        // under the shipped package's identity and paused at the committed tick-0 boundary.
        // The Prepare snapshot is the module's own initial state — a store-level round must
        // carry a payload the host can actually restore or the resume's honest replay fails.
        var roomStore = new RedesignRoomStore(environment.DatabaseConnectionString);
        var identityStore = new RedesignIdentityStore(environment.DatabaseConnectionString);
        var bindingStore = new RedesignBindingStore(environment.DatabaseConnectionString);
        var controlStore = new RedesignControlStore(environment.DatabaseConnectionString);

        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1",
            "sync08-resume-host");
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
            "simulator", "app-1", "platform-room", "live-sync08-resume");
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1",
            "connection-sync08-resume", RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId,
            "game-a", "mount-sync08-resume");
        var claim = await controlStore.ClaimOwnershipAsync("tenant-a", "game-a", "room", Room,
            OwnerInstance, 60);

        var package = RedesignModuleCatalog.Default;
        var module = package.Load(package.ManifestAt(25));
        var initial = module.CreateInitialState(new GameStartConfig(800, 444,
            [new GameParticipant(streamer.StreamerId, GameStartConfig.RedFaction)]));
        var payload = module.Serialize(initial);

        await roomStore.StartRoundAsync("tenant-a", "game-a", Room, OwnerInstance, claim.Epoch,
            [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "start-sync08-resume");
        await roomStore.PrepareRoundAsync("tenant-a", "game-a", Room, OwnerInstance, claim.Epoch,
            payload, module.ComputeStateHash(initial), 1, "prepare-sync08-resume",
            tickMs: 25, recoveryMode: "DeterministicReplay", offlinePolicy: OfflinePolicy);
        await roomStore.ActivateRoundAsync("tenant-a", "game-a", Room, OwnerInstance, claim.Epoch,
            "activate-sync08-resume");
        var paused = await roomStore.PauseRoundAsync("tenant-a", "game-a", Room, OwnerInstance,
            claim.Epoch, "all control sessions offline past the declared grace",
            "pause-sync08-resume", 0, 1);
        Assert.Equal("Paused", paused.Status);

        // The control participant's current session reports ready — the shipped Ready=1
        // declaration is met. An old session would be refused (store level); this is the
        // current session, so the count stands at the threshold.
        var ready = await roomStore.ReportResumeReadyAsync("tenant-a", "game-a",
            streamer.StreamerId, session.SessionId, "ready-sync08-resume");
        Assert.True(ready.Paused);
        Assert.Equal(1, ready.ReadyCount);
        Assert.Equal(1, ready.RequiredReadyCount);

        // The host's resume: the data-plane CAS flips Paused→Running at the frozen head and
        // the recovery adoption starts exactly one actor from the verified head.
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", OwnerInstance);
        using var gameChannel = environment.Channel(gamePort, gameCertificate, "game-server");
        var host = new GameHost.GameHostClient(gameChannel);
        var resumed = await host.ResumeRoomAsync(new ResumeRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = Room,
            OwnerInstanceId = OwnerInstance, Epoch = claim.Epoch
        }, deadline: DateTime.UtcNow.AddSeconds(30));
        Assert.Equal("Running", resumed.Status);
        Assert.True(resumed.ActorRunning);

        // The actor is real: the room's committed head advances past the frozen tick 0. The
        // room stays Running long enough (DurationTicks 800 at 25 ms) for the repeated resume
        // below to find the actor still live.
        var advanced = await PollHeadTickAsync(environment, Room, DateTime.UtcNow.AddSeconds(30));
        Assert.True(advanced > 0, "The resumed actor never ticked past the frozen head.");
        Assert.Equal("Running", await RoomStatusAsync(environment, Room));

        // A repeated resume answers the already-running actor — the no-double-loop guarantee.
        var duplicate = await host.ResumeRoomAsync(new ResumeRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = Room,
            OwnerInstanceId = OwnerInstance, Epoch = claim.Epoch
        }, deadline: DateTime.UtcNow.AddSeconds(30));
        Assert.True(duplicate.ActorRunning);
        Assert.Equal("Running", duplicate.Status);
        Assert.Equal("Running", await RoomStatusAsync(environment, Room));
    }

    private static async Task<long> PollHeadTickAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            var tick = await HeadTickAsync(environment, roomId);
            if (tick > 0) return tick;
            await Task.Delay(100);
        }
        return await HeadTickAsync(environment, roomId);
    }

    private static async Task<long> HeadTickAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT last_tick FROM game.room WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> RoomStatusAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM game.room WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
