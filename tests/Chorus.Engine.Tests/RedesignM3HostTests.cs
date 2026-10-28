using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.Engine.Tests;

/// <summary>
/// M3 host boundary (architecture 7.1): the ticket queue is served by the real DataServer
/// behind the certificate roles — streamers enqueue through their session mounts, the control
/// plane reads candidate windows and sweeps, and coordinator terms live on the real
/// ControlServer. Cross-role and cross-tenant calls refuse visibly.
/// </summary>
public sealed class RedesignM3HostTests
{
    [Fact]
    public async Task TicketsFlowThroughTheRoleBoundaryOnRealHosts()
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

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var mount = await MountAsync(environment, simulator, "m3-host-1");
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";

        // The streamer path: a tenant-scoped streamer-server certificate enqueues its mount.
        using (var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-m3-1"))
        using (var streamerChannel = environment.Channel(environment.DataPort, streamerCertificate, "data-server"))
        {
            var queue = new MatchQueue.MatchQueueClient(streamerChannel);
            var enqueued = await queue.EnqueueTicketAsync(new EnqueueTicketRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId,
                SessionId = mount.SessionId, PartitionKey = partition, OperationId = "m3-enqueue-1"
            });
            Assert.True(enqueued.Found);
            Assert.Equal("Queued", enqueued.Ticket.State);

            var retried = await queue.EnqueueTicketAsync(new EnqueueTicketRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId,
                SessionId = mount.SessionId, PartitionKey = partition, OperationId = "m3-enqueue-1"
            });
            Assert.True(retried.Duplicate);
            Assert.Equal(enqueued.Ticket.TicketId, retried.Ticket.TicketId);

            var status = await queue.GetTicketAsync(new GetTicketRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId
            });
            Assert.True(status.Found);
            Assert.Equal("Queued", status.Ticket.State);

            // Role and tenant refusals are visible, not silent.
            var deniedRole = await Assert.ThrowsAsync<RpcException>(() => queue.ListQueuedAsync(
                new ListQueuedRequest { PartitionKey = partition, Max = 10 }).ResponseAsync);
            Assert.Equal(StatusCode.PermissionDenied, deniedRole.StatusCode);
            var deniedTenant = await Assert.ThrowsAsync<RpcException>(() => queue.EnqueueTicketAsync(
                new EnqueueTicketRequest
                {
                    TenantId = "tenant-b", GameId = "game-a", BindingId = mount.BindingId,
                    SessionId = mount.SessionId, PartitionKey = partition, OperationId = "m3-enqueue-2"
                }).ResponseAsync);
            Assert.Equal(StatusCode.PermissionDenied, deniedTenant.StatusCode);
        }

        // The control path: a cluster-scoped coordinator reads the window and sweeps.
        using (var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-1"))
        using (var controlChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server"))
        {
            var queue = new MatchQueue.MatchQueueClient(controlChannel);
            var window = await queue.ListQueuedAsync(new ListQueuedRequest
            {
                PartitionKey = partition, Max = 10
            });
            Assert.Single(window.Tickets);
            Assert.Equal(mount.BindingId, window.Tickets[0].BindingId);

            var swept = await queue.ExpireQueuedAsync(new ExpireQueuedRequest { Max = 10 });
            Assert.Equal(0, swept.Expired);

            var cancelled = await queue.CancelTicketAsync(new CancelTicketRequest
            {
                TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId,
                OperationId = "m3-cancel-1"
            });
            Assert.True(cancelled.Found);
            Assert.Equal("Cancelled", cancelled.Ticket.State);
            Assert.Empty((await queue.ListQueuedAsync(
                new ListQueuedRequest { PartitionKey = partition, Max = 10 })).Tickets);
        }
    }

    [Fact]
    public async Task CoordinatorTermsAreServedByControlPlaneOnly()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-2");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var coordinator = new Coordinator.CoordinatorClient(controlChannel);

        var claim = await coordinator.ClaimTermAsync(new TermRequest
        {
            PartitionKey = "match/main", HolderInstanceId = "control-m3-2", LeaseSeconds = 30
        });
        Assert.True(claim.Found);
        Assert.Equal(1, claim.Term);

        var renewed = await coordinator.RenewTermAsync(new TermRequest
        {
            PartitionKey = "match/main", HolderInstanceId = "control-m3-2", LeaseSeconds = 30
        });
        Assert.Equal(1, renewed.Term);

        var status = await coordinator.GetTermAsync(new GetTermRequest { PartitionKey = "match/main" });
        Assert.True(status.Found);
        Assert.Equal("control-m3-2", status.HolderInstanceId);

        // A displaced holder renews into a visible refusal, and other roles never touch terms.
        var lost = await Assert.ThrowsAsync<RpcException>(() => coordinator.RenewTermAsync(new TermRequest
        {
            PartitionKey = "match/main", HolderInstanceId = "control-none", LeaseSeconds = 30
        }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, lost.StatusCode);
        using var dataCertificate = environment.Certificate("@cluster", "data-server", "data-m3-2");
        using var dataChannel = environment.Channel(environment.ControlPort, dataCertificate, "control-server");
        var outsider = new Coordinator.CoordinatorClient(dataChannel);
        var denied = await Assert.ThrowsAsync<RpcException>(() => outsider.ClaimTermAsync(new TermRequest
        {
            PartitionKey = "match/main", HolderInstanceId = "data-m3-2", LeaseSeconds = 30
        }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-m3-" + openId;
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
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }
}
