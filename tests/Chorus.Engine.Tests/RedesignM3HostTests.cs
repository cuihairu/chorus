using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using RoutingService = Chorus.Redesign.Rpc.Routing;

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

    [Fact]
    public async Task TheCoordinatorPairsQueuedMountsIntoRoundsWithoutAManualOrchestrator()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulator = await StartMatchClusterAsync(environment);
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        var host = await MountAsync(environment, simulator, "m3-host-5a");
        var guest = await MountAsync(environment, simulator, "m3-host-5b");

        // Two mounts enqueue on their own certificates; the coordinator behind the control
        // plane sees the partition and walks reserve → own → start → prepare → mark → commit.
        await EnqueueAsync(environment, host, partition, "m3-enqueue-5a");
        await EnqueueAsync(environment, guest, partition, "m3-enqueue-5b");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-5");
        using var controlChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(controlChannel);
        var match = new Match.MatchClient(controlChannel);
        Assert.Contains(partition, (await queue.ListPartitionsAsync(new Empty())).PartitionKeys);

        var committed = await WaitForAsync(async () =>
        {
            var hostTicket = await queue.GetTicketAsync(
                new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId });
            var guestTicket = await queue.GetTicketAsync(
                new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId });
            return hostTicket.Ticket.State == "Committed" && guestTicket.Ticket.State == "Committed"
                && hostTicket.Ticket.ReservationId == guestTicket.Ticket.ReservationId
                && hostTicket.Ticket.ReservationId.Length > 0;
        }, TimeSpan.FromSeconds(30));
        Assert.True(committed, "The coordinator did not commit the pair.");

        var hostTicket = await queue.GetTicketAsync(
            new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId });
        var reservation = await match.GetReservationAsync(new GetReservationRequest
        {
            TenantId = "tenant-a", GameId = "game-a", ReservationId = hostTicket.Ticket.ReservationId
        });
        Assert.Equal("Committed", reservation.Reservation.Status);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-m3-5");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var round = await new RoundStore.RoundStoreClient(gameChannel).GetRoundAsync(new GetRoundRequest
        {
            RoomId = reservation.Reservation.RoomId, GameId = "game-a"
        });
        Assert.True(round.Found);
        Assert.Equal(2, round.Participants.Count);
        Assert.Contains(round.Participants, participant => participant.BindingId == host.BindingId);
        Assert.Contains(round.Participants, participant => participant.BindingId == guest.BindingId);

        // A third mount cancels while queued: no partner ever arrives, and the queue drains.
        var loner = await MountAsync(environment, simulator, "m3-host-5c");
        await EnqueueAsync(environment, loner, partition, "m3-enqueue-5c");
        var cancelled = await queue.CancelTicketAsync(new CancelTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = loner.BindingId, OperationId = "m3-cancel-5"
        });
        Assert.Equal("Cancelled", cancelled.Ticket.State);
        var drained = await WaitForAsync(async () =>
            (await queue.ListPartitionsAsync(new Empty())).PartitionKeys.Count == 0, TimeSpan.FromSeconds(30));
        Assert.True(drained, "The partition never drained after the commit and the cancel.");
    }

    [Fact]
    public async Task ASuccessorCoordinatorConvergesOrphanedReservations()
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
        // The first control plane runs without game endpoints: a plain control plane, matching
        // nothing. The test plays the coordinator that dies right after reserving.
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
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        var host = await MountAsync(environment, simulator, "m3-host-6a");
        var guest = await MountAsync(environment, simulator, "m3-host-6b");
        await EnqueueAsync(environment, host, partition, "m3-enqueue-6a");
        await EnqueueAsync(environment, guest, partition, "m3-enqueue-6b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-6");
        using (var controlPlane = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server"))
        {
            var coordinator = new Coordinator.CoordinatorClient(controlPlane);
            var term = await coordinator.ClaimTermAsync(new TermRequest
            {
                PartitionKey = partition, HolderInstanceId = "control-m3-6", LeaseSeconds = 30
            });
            Assert.Equal(1, term.Term);
        }
        using var dataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var match = new Match.MatchClient(dataChannel);
        var orphan = await match.ReserveMatchAsync(new ReserveMatchRequest
        {
            TenantId = "tenant-a", GameId = "game-a", PartitionKey = partition,
            CoordinatorTerm = 1, HolderInstanceId = "control-m3-6", RoomId = "room-m3-6",
            ReservationSeconds = 15, OperationId = "m3-reserve-6",
            Participants =
            {
                new ReservedParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                },
                new ReservedParticipantProto
                {
                    BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest"
                }
            }
        });
        Assert.Equal("Reserved", orphan.Reservation.Status);

        // The coordinator process dies; its successor starts with game endpoints configured.
        environment.Kill("Chorus.ControlServer");
        var coordinatorEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
            ["CHORUS_GAME_ENDPOINTS"] = $"https://localhost:{environment.GamePort}",
            ["CHORUS_MATCH_INTERVAL_MS"] = "200"
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", coordinatorEnvironment);

        // The successor takes over the term, releases the orphan it cannot finish — no second
        // room, no half-enrollment — and pairs the returned tickets into a committed match.
        var queue = new MatchQueue.MatchQueueClient(dataChannel);
        var converged = await WaitForAsync(async () =>
        {
            var hostTicket = await queue.GetTicketAsync(
                new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId });
            var guestTicket = await queue.GetTicketAsync(
                new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId });
            return hostTicket.Ticket.State == "Committed" && guestTicket.Ticket.State == "Committed"
                && hostTicket.Ticket.ReservationId != orphan.Reservation.ReservationId;
        }, TimeSpan.FromSeconds(30));
        Assert.True(converged, "The successor coordinator did not converge the orphan.");

        var retired = await match.GetReservationAsync(new GetReservationRequest
        {
            TenantId = "tenant-a", GameId = "game-a", ReservationId = orphan.Reservation.ReservationId
        });
        Assert.Equal("Aborted", retired.Reservation.Status);
        var hostTicket = await queue.GetTicketAsync(
            new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId });
        var committed = await match.GetReservationAsync(new GetReservationRequest
        {
            TenantId = "tenant-a", GameId = "game-a", ReservationId = hostTicket.Ticket.ReservationId
        });
        Assert.Equal("Committed", committed.Reservation.Status);
        Assert.NotEqual("room-m3-6", committed.Reservation.RoomId);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-m3-6");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var orphanRoom = await new RoundStore.RoundStoreClient(gameChannel).GetRoundAsync(
            new GetRoundRequest { RoomId = "room-m3-6", GameId = "game-a" });
        Assert.False(orphanRoom.Found);
    }

    [Fact]
    public async Task UnsupervisedHostsSelfRegisterIntoTheVersionedRoutingSnapshot()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        // The node exists because an agent registered it; the hosts then report themselves
        // into that node — the certificate must be the claimed instance (architecture 4.4).
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m3-7"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m3-7" });
        }

        using var routingCertificate = environment.Certificate("@cluster", "control-server", "control-m3-7");
        using var routingChannel = environment.Channel(environment.ControlPort, routingCertificate, "control-server");
        var routing = new RoutingService.RoutingClient(routingChannel);
        var before = await routing.GetRoutingSnapshotAsync(new Empty());
        Assert.Empty(before.Instances);
        Assert.Empty(before.Rooms);
        Assert.Empty(before.Streamers);

        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, null, "500"),
            clientTenant: "tenant-a", instanceId: "game-m3-7a");
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, "node-a/game-server#2", "500"),
            clientTenant: "tenant-a", instanceId: "game-m3-7b");

        var registered = await WaitForAsync(async () =>
            (await routing.GetRoutingSnapshotAsync(new Empty())).Instances.Count == 2, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The self-reporting hosts never appeared in the routing snapshot.");

        var current = await routing.GetRoutingSnapshotAsync(new Empty());
        Assert.NotEqual(before.Version, current.Version);
        Assert.Contains(current.Instances, instance => instance.ProcessInstanceId == "game-m3-7a"
            && instance.Role == "game-server" && instance.NodeId == "node-a"
            && instance.SlotId == "node-a/game-server" && instance.Endpoint == $"https://localhost:{gamePortA}"
            && instance.Readiness == "Ready" && instance.Admission == "Accepting");
        Assert.Contains(current.Instances, instance => instance.ProcessInstanceId == "game-m3-7b"
            && instance.SlotId == "node-a/game-server#2" && instance.Endpoint == $"https://localhost:{gamePortB}"
            && instance.Readiness == "Ready");
        // The version hashes routed content, not time: a quiet cluster keeps its version.
        Assert.Equal(current.Version, (await routing.GetRoutingSnapshotAsync(new Empty())).Version);

        // A host cannot claim another role's slot report — the refusal is visible.
        using var impostor = environment.Certificate("@cluster", "streamer-server", "impostor-m3-7");
        using var impostorChannel = environment.Channel(environment.ControlPort, impostor, "control-server");
        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            new ControlService.ControlServiceClient(impostorChannel).ReportProcessAsync(new ReportProcessRequest
            {
                NodeId = "node-a", AgentInstanceId = "game-m3-7a", SlotId = "node-a/game-server",
                ProcessInstanceId = "game-m3-7a", ReportSequence = 99, Lifecycle = "Running",
                Readiness = "Ready", Health = "Healthy", Admission = "Open", EntityRecovery = "Steady",
                StartedUnixMs = 1, Role = "game-server", Endpoint = "https://localhost:1"
            }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
    }

    [Fact]
    public async Task RoomsSpreadAcrossInstancesSurfaceInSnapshotsAndWatchNotices()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var endpointA = $"https://localhost:{gamePortA}";
        var endpointB = $"https://localhost:{gamePortB}";
        const string instanceA = "game-m3-8a";
        const string instanceB = "game-m3-8b";
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
        var coordinatorEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
            // Named host instances: the coordinator owns rooms as these ids; the routing
            // snapshot joins them back to the endpoints the hosts self-reported.
            ["CHORUS_GAME_ENDPOINTS"] = $"{instanceA}={endpointA},{instanceB}={endpointB}",
            ["CHORUS_MATCH_INTERVAL_MS"] = "200"
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", coordinatorEnvironment);
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m3-8"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m3-8" });
        }
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";
        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, null, "500"),
            clientTenant: "tenant-a", instanceId: instanceA);
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, "node-a/game-server#2", "500"),
            clientTenant: "tenant-a", instanceId: instanceB);

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        var mounts = new List<(string StreamerId, string SessionId, string BindingId)>();
        for (var index = 0; index < 4; index++)
            mounts.Add(await MountAsync(environment, simulator, $"m3-host-8{index}"));
        foreach (var (mount, index) in mounts.Select((mount, index) => (mount, index)))
            await EnqueueAsync(environment, mount, partition, $"m3-enqueue-8{index}");

        // Four mounts, two pairs; the round-robin coordinator spreads them over both hosts.
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-8");
        using var dataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(dataChannel);
        var settled = await WaitForAsync(async () =>
        {
            foreach (var mount in mounts)
            {
                var ticket = await queue.GetTicketAsync(
                    new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId });
                if (ticket.Ticket.State != "Committed") return false;
            }
            return true;
        }, TimeSpan.FromSeconds(30));
        Assert.True(settled, "The coordinator did not commit both pairs.");

        using var routingChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        var routing = new RoutingService.RoutingClient(routingChannel);
        var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
        Assert.Equal(2, snapshot.Rooms.Count);
        Assert.All(snapshot.Rooms, room =>
        {
            Assert.True(room.Epoch > 0, "A live room carries its ownership epoch.");
            Assert.Equal("Active", room.Status);
            Assert.True(room.OwnerInstanceId == instanceA || room.OwnerInstanceId == instanceB,
                "The owner is one of the configured hosts.");
            // The self-reported endpoint join: the owner resolves to the address it serves on.
            Assert.Equal(room.OwnerInstanceId == instanceA ? endpointA : endpointB, room.Endpoint);
        });
        Assert.Equal(2, snapshot.Rooms.Select(room => room.OwnerInstanceId).Distinct().Count());

        // Streamer routes: every mount is bound to the room its pair committed, two per room.
        Assert.Equal(4, snapshot.Streamers.Count);
        var roomIds = snapshot.Rooms.Select(room => room.RoomId).ToHashSet();
        foreach (var roomId in roomIds)
            Assert.Equal(2, snapshot.Streamers.Count(route => route.RoomId == roomId));
        foreach (var mount in mounts)
        {
            var routes = snapshot.Streamers.Where(route => route.BindingId == mount.BindingId).ToList();
            Assert.Single(routes);
            Assert.Equal(mount.StreamerId, routes[0].StreamerId);
            Assert.Contains(routes[0].RoomId, roomIds);
        }

        // Instances carry the endpoints the coordinator wrote as owners.
        Assert.Equal(2, snapshot.Instances.Count);
        Assert.Contains(snapshot.Instances, instance =>
            instance.ProcessInstanceId == instanceA && instance.Endpoint == endpointA);
        Assert.Contains(snapshot.Instances, instance =>
            instance.ProcessInstanceId == instanceB && instance.Endpoint == endpointB);

        // The watch (architecture 4.4): a new mount changes the routed content, the notice
        // arrives, and the consumer answers it with a full pull — break or gap means re-pull.
        var version = snapshot.Version;
        using var call = routing.WatchRouting(new WatchRoutingRequest { KnownVersion = version });
        var fifth = await MountAsync(environment, simulator, "m3-host-8x");
        long noticed = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (noticed == 0 && DateTime.UtcNow < deadline)
        {
            using var tick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (await call.ResponseStream.MoveNext(tick.Token))
                    noticed = call.ResponseStream.Current.Version;
            }
            catch (RpcException) when (tick.IsCancellationRequested) { }
        }
        Assert.NotEqual(0, noticed);
        Assert.NotEqual(version, noticed);
        var repulled = await routing.GetRoutingSnapshotAsync(new Empty());
        Assert.Equal(noticed, repulled.Version);
        Assert.Contains(repulled.Streamers, route => route.BindingId == fifth.BindingId);
    }

    private static Dictionary<string, string> GameEnvironment(
        int dataPort, string controlEndpoint, string? slotId, string reportIntervalMs)
    {
        var variables = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = dataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
            ["CHORUS_REPORT_INTERVAL_MS"] = reportIntervalMs
        };
        if (slotId is not null) variables["CHORUS_SLOT_ID"] = slotId;
        return variables;
    }

    [Fact]
    public async Task PlanCommandsAreIdempotentOnTheNodeAgent()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var agent = await environment.StartBootstrapAgentAsync(joinCluster: true);
        await environment.WaitForAgentReportsAsync(agent, expectedPreviousAgent: null, nodeId: "node-a");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-10");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var deployment = new Deployment.DeploymentClient(controlChannel);
        const string dataSlot = "node-a/data-server";

        var before = environment.AgentJournal().Single(entry => entry.SlotId == dataSlot);
        Assert.True(before.Pid > 0);

        // Drain the data slot: the agent stops it gracefully, journals the command first,
        // and the slot stays down — the supervisor does not restart what a plan stopped.
        var drained = await deployment.ApplyPlanAsync(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = dataSlot, Role = "data-server",
            DesiredState = "Draining", DesiredVersion = "bootstrap", PackageHash = "bootstrap",
            PlanGeneration = 5, CommandId = "m3-drain-data-1"
        });
        Assert.Equal(5, drained.PlanGeneration);
        var stopped = await WaitForAsync(() => Task.FromResult(environment.AgentJournal()
            .Single(entry => entry.SlotId == dataSlot) is { Pid: 0, DesiredState: "Draining" }
            && IsProcessGone(before.Pid)), TimeSpan.FromSeconds(30));
        Assert.True(stopped, "The agent did not drain the data slot.");
        Assert.Equal("m3-drain-data-1", environment.AgentJournal()
            .Single(entry => entry.SlotId == dataSlot).CommandId);

        // A re-delivered command id is a visible no-op on the node.
        var repeated = await deployment.ApplyPlanAsync(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = dataSlot, Role = "data-server",
            DesiredState = "Draining", DesiredVersion = "bootstrap", PackageHash = "bootstrap",
            PlanGeneration = 5, CommandId = "m3-drain-data-1"
        });
        Assert.Equal(5, repeated.PlanGeneration);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(0, environment.AgentJournal().Single(entry => entry.SlotId == dataSlot).Pid);

        // An expired command is refused even at a fresh generation.
        var expired = await deployment.ApplyPlanAsync(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = dataSlot, Role = "data-server",
            DesiredState = "Stopped", DesiredVersion = "bootstrap", PackageHash = "bootstrap",
            PlanGeneration = 6, CommandId = "m3-late-stop-1",
            CommandExpiresUnixMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds()
        });
        Assert.Equal(6, expired.PlanGeneration);
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Running again restarts the slot exactly once, with a fresh instance.
        var restarted = await deployment.ApplyPlanAsync(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = dataSlot, Role = "data-server",
            DesiredState = "Running", DesiredVersion = "bootstrap", PackageHash = "bootstrap",
            PlanGeneration = 7, CommandId = "m3-start-data-1"
        });
        Assert.Equal(7, restarted.PlanGeneration);
        var running = await WaitForAsync(() => Task.FromResult(environment.AgentJournal()
            .Single(entry => entry.SlotId == dataSlot) is { Pid: > 0, DesiredState: "Running" }
            && !IsProcessGone(environment.AgentJournal().Single(entry => entry.SlotId == dataSlot).Pid)),
            TimeSpan.FromSeconds(30));
        Assert.True(running, "The agent did not restart the data slot.");

        // A stale generation never rewinds: the store answers with the plan that won and the
        // node keeps its running slot.
        var stale = await deployment.ApplyPlanAsync(new ApplyPlanRequest
        {
            NodeId = "node-a", SlotId = dataSlot, Role = "data-server",
            DesiredState = "Stopped", DesiredVersion = "bootstrap", PackageHash = "bootstrap",
            PlanGeneration = 3, CommandId = "m3-stale-stop-1"
        });
        Assert.Equal(7, stale.PlanGeneration);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.True(environment.AgentJournal().Single(entry => entry.SlotId == dataSlot).Pid > 0);
    }

    [Fact]
    public async Task ControlOutagePausesMatchingAndPlansDoNotRepullProcesses()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        var agentEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_GAME_ENDPOINTS"] = $"https://localhost:{gamePort}",
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
            ["CHORUS_MATCH_INTERVAL_MS"] = "200",
            ["CHORUS_GAME_CLIENT_TENANT"] = "tenant-a"
        };
        // The whole match path on one supervised node: control (coordinator) + data + game.
        var agent = await environment.StartBootstrapAgentAsync("Chorus.GameServer", gamePort,
            toolEnvironment: null, agentEnvironment, joinCluster: true);
        await environment.WaitForAgentReportsAsync(agent, expectedPreviousAgent: null, expectedReady: 3,
            nodeId: "node-a");

        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        var first = await MountAsync(environment, simulator, "m3-host-10a");
        var second = await MountAsync(environment, simulator, "m3-host-10b");
        await EnqueueAsync(environment, first, partition, "m3-enqueue-10a");
        await EnqueueAsync(environment, second, partition, "m3-enqueue-10b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-11");
        using var dataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(dataChannel);
        var committed = await WaitForAsync(async () =>
        {
            var a = await queue.GetTicketAsync(
                new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = first.BindingId });
            var b = await queue.GetTicketAsync(
                new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = second.BindingId });
            return a.Ticket.State == "Committed" && b.Ticket.State == "Committed"
                && a.Ticket.ReservationId == b.Ticket.ReservationId;
        }, TimeSpan.FromSeconds(30));
        Assert.True(committed, "The agent-hosted coordinator did not commit the pair.");

        var reservation = await new Match.MatchClient(dataChannel).GetReservationAsync(
            new GetReservationRequest { TenantId = "tenant-a", GameId = "game-a", ReservationId =
                (await queue.GetTicketAsync(new GetTicketRequest
                { TenantId = "tenant-a", GameId = "game-a", BindingId = first.BindingId })).Ticket.ReservationId });
        var roomId = reservation.Reservation.RoomId;

        // The committed room finishes on the data plane by contract — no control plane needed.
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-m3-11");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(30));
        Assert.True(finished, "The committed round never finished on the data plane.");

        var instances = await SlotInstancesAsync(environment);
        var journal = environment.AgentJournal().ToDictionary(entry => entry.SlotId);
        var controlPid = journal["node-a/control-server"].Pid;

        // The control plane freezes (alive, deaf). The data plane keeps serving; matching
        // pauses once the coordinator term lease expires — the queued pair stays queued.
        _ = kill(controlPid, 19);
        var third = await MountAsync(environment, simulator, "m3-host-10c");
        var fourth = await MountAsync(environment, simulator, "m3-host-10d");
        await EnqueueAsync(environment, third, partition, "m3-enqueue-10c");
        await EnqueueAsync(environment, fourth, partition, "m3-enqueue-10d");
        await Task.Delay(TimeSpan.FromSeconds(20));
        var thirdTicket = await queue.GetTicketAsync(
            new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = third.BindingId });
        var fourthTicket = await queue.GetTicketAsync(
            new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = fourth.BindingId });
        Assert.Equal("Queued", thirdTicket.Ticket.State);
        Assert.Equal("Queued", fourthTicket.Ticket.State);
        // The data plane still serves reads for the finished room while control is deaf.
        var duringOutage = await rounds.GetRoundAsync(new GetRoundRequest { RoomId = roomId, GameId = "game-a" });
        Assert.True(duringOutage.Found);

        // The frozen control process dies for real (no SIGCONT — the old coordinator never
        // wakes to pair the queue); the agent restarts it and re-receives the plans, without
        // re-pulling the data and game slots it already supervises.
        _ = kill(controlPid, 9);
        var recovered = await WaitForAsync(async () =>
        {
            var current = await SlotInstancesAsync(environment);
            return current["node-a/control-server"] != instances["node-a/control-server"]
                && current["node-a/data-server"] == instances["node-a/data-server"]
                && current["node-a/game-server"] == instances["node-a/game-server"];
        }, TimeSpan.FromSeconds(40));
        Assert.True(recovered, "The agent did not restart control without touching its siblings.");

        var afterJournal = environment.AgentJournal().ToDictionary(entry => entry.SlotId);
        Assert.Equal(journal["node-a/data-server"].Pid, afterJournal["node-a/data-server"].Pid);
        Assert.Equal(journal["node-a/game-server"].Pid, afterJournal["node-a/game-server"].Pid);

        // The successor coordinator takes the term and pairs the paused queue.
        var thirdBinding = third.BindingId;
        var fourthBinding = fourth.BindingId;
        var resumed = await WaitForAsync(async () =>
        {
            var thirdTicket = await queue.GetTicketAsync(new GetTicketRequest
                { TenantId = "tenant-a", GameId = "game-a", BindingId = thirdBinding });
            var fourthTicket = await queue.GetTicketAsync(new GetTicketRequest
                { TenantId = "tenant-a", GameId = "game-a", BindingId = fourthBinding });
            return thirdTicket.Ticket.State == "Committed" && fourthTicket.Ticket.State == "Committed"
                && thirdTicket.Ticket.ReservationId == fourthTicket.Ticket.ReservationId;
        }, TimeSpan.FromSeconds(60));
        Assert.True(resumed, "The successor coordinator did not pair the paused queue.");
    }

    private static bool IsProcessGone(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    /// <summary>The newest process instance per slot of the supervised node.</summary>
    private static async Task<Dictionary<string, string>> SlotInstancesAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
        await using var connection = new Npgsql.NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT DISTINCT ON (slot_id) slot_id,process_instance_id
            FROM control.process WHERE node_id='node-a'
            ORDER BY slot_id, observed_at DESC
            """, connection);
        var result = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static async Task<DevelopmentPlatformSimulator> StartMatchClusterAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
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
        var coordinatorEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
            ["CHORUS_GAME_ENDPOINTS"] = $"https://localhost:{environment.GamePort}",
            ["CHORUS_MATCH_INTERVAL_MS"] = "200"
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", coordinatorEnvironment);
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");
        return new DevelopmentPlatformSimulator(simulatorKey);
    }

    private static async Task EnqueueAsync(RedesignM0HostTests.HostEnvironment environment,
        (string StreamerId, string SessionId, string BindingId) mount, string partition, string operationId)
    {
        using var certificate = environment.Certificate("tenant-a", "streamer-server",
            "streamer-" + operationId);
        using var channel = environment.Channel(environment.DataPort, certificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(channel);
        await queue.EnqueueTicketAsync(new EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId,
            SessionId = mount.SessionId, PartitionKey = partition, OperationId = operationId
        });
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(200);
        }
        return await probe();
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
