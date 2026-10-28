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

    [Fact]
    public async Task ReservationsFlowThroughTheControlPlaneOnly()
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
        var mount = await MountAsync(environment, simulator, "m3-host-3");
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";

        // The coordinator claims its term on the control plane first.
        using (var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-3"))
        {
            using (var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
                "control-server"))
            {
                var coordinator = new Coordinator.CoordinatorClient(controlChannel);
                var term = await coordinator.ClaimTermAsync(new TermRequest
                {
                    PartitionKey = partition, HolderInstanceId = "control-m3-3", LeaseSeconds = 30
                });
                Assert.Equal(1, term.Term);
            }

            // The streamer enqueues through its own certificate.
            using (var streamerCertificate = environment.Certificate("tenant-a", "streamer-server",
                "streamer-m3-3"))
            using (var streamerChannel = environment.Channel(environment.DataPort, streamerCertificate,
                "data-server"))
            {
                var queue = new MatchQueue.MatchQueueClient(streamerChannel);
                var enqueued = await queue.EnqueueTicketAsync(new EnqueueTicketRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId,
                    SessionId = mount.SessionId, PartitionKey = partition, OperationId = "m3-enqueue-3"
                });
                Assert.Equal("Queued", enqueued.Ticket.State);

                // Only the control plane reserves or commits; a streamer certificate never does.
                var match = new Match.MatchClient(streamerChannel);
                var denied = await Assert.ThrowsAsync<RpcException>(() => match.ReserveMatchAsync(
                    new ReserveMatchRequest
                    {
                        TenantId = "tenant-a", GameId = "game-a", PartitionKey = partition,
                        CoordinatorTerm = 1, HolderInstanceId = "control-m3-3", RoomId = "room-m3-3",
                        ReservationSeconds = 30, OperationId = "m3-reserve-1",
                        Participants = { new ReservedParticipantProto
                        {
                            BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
                        } }
                    }).ResponseAsync);
                Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
                var deniedCommit = await Assert.ThrowsAsync<RpcException>(() => match.CommitMatchAsync(
                    new CommitMatchRequest
                    {
                        TenantId = "tenant-a", GameId = "game-a", ReservationId = "reservation-none",
                        CoordinatorTerm = 1, HolderInstanceId = "control-m3-3", RoomInstanceId = "gs-1",
                        RoomEpoch = 1, OperationId = "m3-commit-0"
                    }).ResponseAsync);
                Assert.Equal(StatusCode.PermissionDenied, deniedCommit.StatusCode);
            }

            // The coordinator reserves under its term on the data plane; a stale term refuses.
            using (var dataChannel = environment.Channel(environment.DataPort, controlCertificate,
                "data-server"))
            {
                var match = new Match.MatchClient(dataChannel);
                var stale = await Assert.ThrowsAsync<RpcException>(() => match.ReserveMatchAsync(
                    new ReserveMatchRequest
                    {
                        TenantId = "tenant-a", GameId = "game-a", PartitionKey = partition,
                        CoordinatorTerm = 7, HolderInstanceId = "control-m3-3", RoomId = "room-m3-3",
                        ReservationSeconds = 30, OperationId = "m3-reserve-2",
                        Participants = { new ReservedParticipantProto
                        {
                            BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
                        } }
                    }).ResponseAsync);
                Assert.Equal(StatusCode.FailedPrecondition, stale.StatusCode);

                var reserved = await match.ReserveMatchAsync(new ReserveMatchRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", PartitionKey = partition,
                    CoordinatorTerm = 1, HolderInstanceId = "control-m3-3", RoomId = "room-m3-3",
                    ReservationSeconds = 1, OperationId = "m3-reserve-3",
                    Participants = { new ReservedParticipantProto
                    {
                        BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
                    } }
                });
                Assert.True(reserved.Found);
                Assert.Equal("Reserved", reserved.Reservation.Status);
                Assert.Equal("room-m3-3", reserved.Reservation.RoomId);
                Assert.Equal(1, reserved.Reservation.CoordinatorTerm);
                Assert.Single(reserved.Reservation.Participants);

                // A second match walks the whole path — reserve, claim the room ownership on
                // the control plane, prepare, commit — and the sweep only retires the first.
                var guest = await MountAsync(environment, simulator, "m3-host-4");
                var queue = new MatchQueue.MatchQueueClient(dataChannel);
                await queue.EnqueueTicketAsync(new EnqueueTicketRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId,
                    SessionId = guest.SessionId, PartitionKey = partition, OperationId = "m3-enqueue-4"
                });
                using var controlPlane = environment.Channel(environment.ControlPort, controlCertificate,
                    "control-server");
                var ownership = new ControlService.ControlServiceClient(controlPlane);
                var roomClaim = await ownership.ClaimOwnershipAsync(new OwnershipRequest
                {
                    Kind = "room", EntityId = "room-m3-4", OwnerInstanceId = "gs-m3-4",
                    LeaseSeconds = 30, TenantId = "tenant-a", GameId = "game-a"
                });
                var walk = await match.ReserveMatchAsync(new ReserveMatchRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", PartitionKey = partition,
                    CoordinatorTerm = 1, HolderInstanceId = "control-m3-3", RoomId = "room-m3-4",
                    ReservationSeconds = 30, OperationId = "m3-reserve-4",
                    Participants = { new ReservedParticipantProto
                    {
                        BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Host"
                    } }
                });
                var wrongEpoch = await Assert.ThrowsAsync<RpcException>(() => match.CommitMatchAsync(
                    new CommitMatchRequest
                    {
                        TenantId = "tenant-a", GameId = "game-a",
                        ReservationId = walk.Reservation.ReservationId,
                        CoordinatorTerm = 1, HolderInstanceId = "control-m3-3", RoomInstanceId = "gs-m3-4",
                        RoomEpoch = roomClaim.Epoch + 9, OperationId = "m3-commit-4"
                    }).ResponseAsync);
                Assert.Equal(StatusCode.FailedPrecondition, wrongEpoch.StatusCode);
                await match.MarkPreparedAsync(new MarkPreparedRequest
                {
                    TenantId = "tenant-a", GameId = "game-a",
                    ReservationId = walk.Reservation.ReservationId,
                    CoordinatorTerm = 1, HolderInstanceId = "control-m3-3",
                    PreparedReceiptJson = """{"tick":0}""", OperationId = "m3-prepare-4"
                });
                var committed = await match.CommitMatchAsync(new CommitMatchRequest
                {
                    TenantId = "tenant-a", GameId = "game-a",
                    ReservationId = walk.Reservation.ReservationId,
                    CoordinatorTerm = 1, HolderInstanceId = "control-m3-3", RoomInstanceId = "gs-m3-4",
                    RoomEpoch = roomClaim.Epoch, OperationId = "m3-commit-4"
                });
                Assert.Equal("Committed", committed.Reservation.Status);
                var guestTicket = await queue.GetTicketAsync(new GetTicketRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId
                });
                Assert.Equal("Committed", guestTicket.Ticket.State);
                var aborted = await Assert.ThrowsAsync<RpcException>(() => match.AbortReservationAsync(
                    new AbortReservationRequest
                    {
                        TenantId = "tenant-a", GameId = "game-a",
                        ReservationId = walk.Reservation.ReservationId,
                        CoordinatorTerm = 1, HolderInstanceId = "control-m3-3", OperationId = "m3-abort-4"
                    }).ResponseAsync);
                Assert.Equal(StatusCode.FailedPrecondition, aborted.StatusCode);

                // The deadline passes; the sweep retires only the first reservation.
                await Task.Delay(TimeSpan.FromMilliseconds(1500));
                var swept = await match.ExpireReservationsAsync(new ExpireReservationsRequest { Max = 10 });
                Assert.Equal(1, swept.Expired);
                var retired = await match.GetReservationAsync(new GetReservationRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", ReservationId = reserved.Reservation.ReservationId
                });
                Assert.True(retired.Found);
                Assert.Equal("Expired", retired.Reservation.Status);
            }
        }
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
