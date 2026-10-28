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
