using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Platform;
using Grpc.Core;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-LINK-03 over real hosts: the StreamerServer submits a mount's match intent through the
/// control plane, which gates on the registered route source (FIX-END-02) before forwarding the
/// ticket to the queue the coordinator scans. The entry forwards a verified mount's intent to a
/// Queued ticket, refuses an unregistered mount and a stale generation, and the mount-time
/// submission is opt-in (CHORUS_MATCH_INTENT_ON_MOUNT) — off by default so the collaboration
/// loop's round boundary (FIX-LINK-04/05) decides when the business path submits on its own.
/// </summary>
public sealed class RedesignMatchIntentHostTests
{
    [Fact]
    public async Task SubmitMatchIntentForwardsAQueuedTicketAndTheGateRefusesUnregisteredAndStale()
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
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string> { ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}" });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "intent-a", "live-key-intent-a", "intent-op-1");
        await WaitForRowAsync(environment, host.streamer);

        // The business path's intent: a tenant streamer-server cert submits through the control
        // plane, the gate passes (the mount is the registered route source), the ticket lands.
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server",
            "streamer-intent-a");
        using var controlChannel = environment.Channel(environment.ControlPort, streamerCertificate,
            "control-server");
        var intent = new Chorus.Rpc.MatchIntent.MatchIntentClient(controlChannel);
        var submitted = await intent.SubmitMatchIntentAsync(new Chorus.Rpc.SubmitMatchIntentRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = host.binding,
            BindingGeneration = host.generation, SessionId = host.session,
            PartitionKey = "tenant-a/game-a/-/default", OperationId = "intent-op-1"
        });
        Assert.True(submitted.Found);
        Assert.Equal("Queued", submitted.Ticket.State);
        Assert.Equal(host.binding, submitted.Ticket.BindingId);

        // The ticket is the queue's — visible from the data plane by the mount.
        var queued = await ReadTicketAsync(environment, host.binding);
        Assert.True(queued.found);
        Assert.Equal("Queued", queued.state);

        // A mount that never registered a route source establishes no intent.
        var unregistered = await Assert.ThrowsAsync<RpcException>(() =>
            intent.SubmitMatchIntentAsync(new Chorus.Rpc.SubmitMatchIntentRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = "binding-not-registered",
                BindingGeneration = 1, SessionId = host.session,
                PartitionKey = "tenant-a/game-a/-/default", OperationId = "intent-op-2"
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, unregistered.StatusCode);

        // The next mount supersedes the route source; the previous mount's intent is late
        // evidence now (its binding is no longer the registered source).
        var next = await MountAsync(environment, simulator, "intent-a", "live-key-intent-a",
            "intent-op-2");
        await WaitForRowAsync(environment, host.streamer, row => row.generation >= 2);
        var staleBinding = await Assert.ThrowsAsync<RpcException>(() =>
            intent.SubmitMatchIntentAsync(new Chorus.Rpc.SubmitMatchIntentRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = host.binding,
                BindingGeneration = host.generation, SessionId = host.session,
                PartitionKey = "tenant-a/game-a/-/default", OperationId = "intent-op-3"
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, staleBinding.StatusCode);

        // The current binding at the wrong generation is stale too: the row holds generation 2.
        var staleGeneration = await Assert.ThrowsAsync<RpcException>(() =>
            intent.SubmitMatchIntentAsync(new Chorus.Rpc.SubmitMatchIntentRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = next.binding,
                BindingGeneration = 1, SessionId = next.session,
                PartitionKey = "tenant-a/game-a/-/default", OperationId = "intent-op-4"
            }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, staleGeneration.StatusCode);
    }

    [Fact]
    public async Task AMountSubmitsNoIntentByDefault()
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
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string> { ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}" });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "intent-b", "live-key-intent-b", "intent-op-5");
        await WaitForRowAsync(environment, host.streamer);
        // The default leaves the mount flow exactly as it was: no intent is submitted.
        var ticket = await ReadTicketAsync(environment, host.binding);
        Assert.False(ticket.found);
    }

    [Fact]
    public async Task AMountSubmitsItsIntentWhenEnabled()
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
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}""",
            ["CHORUS_MATCH_INTENT_ON_MOUNT"] = "true"
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string> { ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}" });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "intent-c", "live-key-intent-c", "intent-op-6");
        // The mount submits its intent after registering the route source; the ticket is the
        // queue's once the submit round-trips through the control plane.
        var ticket = await WaitForTicketAsync(environment, host.binding);
        Assert.Equal("Queued", ticket.state);
        Assert.Equal(host.binding, ticket.binding);
    }

    private static async Task<(string streamer, string binding, long generation, string session)> MountAsync(
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
        return (login.StreamerId, mounted.BindingId, mounted.Generation, login.SessionId);
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

    private static async Task<(bool found, string binding, string state)> ReadTicketAsync(
        RedesignM0HostTests.HostEnvironment environment, string bindingId)
    {
        using var certificate = environment.Certificate("@cluster", "control-server", "control-intent-read");
        using var channel = environment.Channel(environment.DataPort, certificate, "data-server");
        var reply = await new Chorus.Rpc.MatchQueue.MatchQueueClient(channel).GetTicketAsync(
            new Chorus.Rpc.GetTicketRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = bindingId
            });
        return reply.Found
            ? (true, reply.Ticket.BindingId, reply.Ticket.State)
            : (false, "", "");
    }

    private static async Task<(bool found, string binding, string state)> WaitForTicketAsync(
        RedesignM0HostTests.HostEnvironment environment, string bindingId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var ticket = await ReadTicketAsync(environment, bindingId);
            if (ticket.found) return ticket;
            await Task.Delay(200);
        }
        var last = await ReadTicketAsync(environment, bindingId);
        Assert.True(last.found, $"The match ticket for {bindingId} never appeared.\n{environment.AllHostOutput}");
        return last;
    }
}
