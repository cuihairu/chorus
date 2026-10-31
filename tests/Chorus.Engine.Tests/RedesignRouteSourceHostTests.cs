using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Platform;
using Grpc.Core;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-02 over real hosts: the StreamerServer reports each committed mount to the
/// control plane as the mount's route source — the registration the end sequence's close
/// commands later bind their BindingGenerations to. The control plane derives the platform
/// identity from the verified live session join: the row carries the identity the platform
/// login verified, never anything the reporter claims. The routing snapshot's streamer rows
/// now carry the binding generation, a duplicate report replays the one row, a foreign
/// reporter is refused, and a late report of the previous mount never overwrites the
/// registration of the mount that followed it.
/// </summary>
public sealed class RedesignRouteSourceHostTests
{
    [Fact]
    public async Task ACommittedMountRegistersItsVerifiedRouteSourceAndSurvivesTheNextMount()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
            ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{environment.StreamerPort}",
            ["CHORUS_REPORT_INTERVAL_MS"] = "500",
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
        var host = await MountAsync(environment, simulator, "route-source-a", "live-key-route-a",
            "mount-route-1");
        // The registrar fires after the mount commits; the row is its evidence.
        var registered = await WaitForRowAsync(environment, host.streamer);
        Assert.Equal(host.binding, registered.binding);
        Assert.Equal(1, registered.generation);

        // The snapshot carries the generation on the streamer row the gateway resolves by.
        using var controlCertificate = environment.Certificate("@cluster", "control-server",
            "control-route-source");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        var routing = new Chorus.Rpc.Routing.RoutingClient(controlChannel);
        var snapshot = await routing.GetRoutingSnapshotAsync(new Chorus.Rpc.Empty());
        var streamerRow = snapshot.Streamers.Single(row => row.BindingId == host.binding);
        Assert.Equal(1, streamerRow.BindingGeneration);
        Assert.Equal("simulator", streamerRow.Platform);
        Assert.Equal("live-key-route-a", streamerRow.CanonicalLiveSessionKey);

        // A replay through the RPC face answers the one row as a duplicate; a caller naming
        // another streamer's mount is refused — the identity is not his to register.
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server",
            "streamer-route-source");
        using var streamerChannel = environment.Channel(environment.ControlPort, streamerCertificate,
            "control-server");
        var registrar = new Chorus.Rpc.Routing.RoutingClient(streamerChannel);
        var replay = await registrar.RegisterRouteSourceAsync(new Chorus.Rpc.RegisterRouteSourceRequest
        {
            TenantId = "tenant-a", GameId = "game-a", StreamerId = host.streamer,
            BindingId = host.binding, BindingGeneration = 1
        });
        Assert.True(replay.Duplicate);
        Assert.Equal("simulator", replay.Platform);
        Assert.Equal("live-key-route-a", replay.CanonicalLiveSessionKey);
        var foreign = await Assert.ThrowsAsync<RpcException>(() => registrar.RegisterRouteSourceAsync(
            new Chorus.Rpc.RegisterRouteSourceRequest
            {
                TenantId = "tenant-a", GameId = "game-a", StreamerId = "streamer-not-his",
                BindingId = host.binding, BindingGeneration = 1
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, foreign.StatusCode);

        // The next mount registers itself at the newer generation; the previous mount's
        // report is late evidence now and never overwrites it.
        var next = await MountAsync(environment, simulator, "route-source-a", "live-key-route-a",
            "mount-route-2");
        Assert.NotEqual(host.binding, next.binding);
        var advanced = await WaitForRowAsync(environment, host.streamer,
            row => row.generation >= 2);
        Assert.Equal(next.binding, advanced.binding);
        Assert.Equal(2, advanced.generation);
        var late = await Assert.ThrowsAsync<RpcException>(() => registrar.RegisterRouteSourceAsync(
            new Chorus.Rpc.RegisterRouteSourceRequest
            {
                TenantId = "tenant-a", GameId = "game-a", StreamerId = host.streamer,
                BindingId = host.binding, BindingGeneration = 1
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, late.StatusCode);
        var afterLate = await ReadRowAsync(environment, host.streamer);
        Assert.Equal((next.binding, 2L), (afterLate.binding, afterLate.generation));
    }

    private static async Task<(string streamer, string binding, long generation)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId, string liveKey, string operationId)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new Chorus.Rpc.StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new Chorus.Rpc.LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + operationId,
            NextResumeSecret = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new Chorus.Rpc.StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = operationId
        });
        return (login.StreamerId, mounted.BindingId, mounted.Generation);
    }

    private static async Task<(string binding, long generation)> WaitForRowAsync(
        RedesignM0HostTests.HostEnvironment environment, string streamerId,
        Func<(string binding, long generation), bool>? ready = null)
    {
        ready ??= _ => true;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var row = await ReadRowAsync(environment, streamerId);
                if (row.binding.Length > 0 && ready(row)) return row;
            }
            catch (NpgsqlException)
            {
                // The row appears with the registrar's commit; retry until the deadline.
            }
            await Task.Delay(200);
        }
        var last = await ReadRowAsync(environment, streamerId);
        Assert.True(last.binding.Length > 0 && ready(last),
            $"The route source row for {streamerId} did not appear: {last.binding} at {last.generation}.");
        return last;
    }

    private static async Task<(string binding, long generation)> ReadRowAsync(
        RedesignM0HostTests.HostEnvironment environment, string streamerId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT binding_id,binding_generation FROM control.route_source
            WHERE tenant_id='tenant-a' AND game_id='game-a' AND streamer_id=@streamer
            """, connection);
        command.Parameters.AddWithValue("streamer", streamerId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return ("", 0);
        return (reader.GetString(0), reader.GetInt64(1));
    }
}
