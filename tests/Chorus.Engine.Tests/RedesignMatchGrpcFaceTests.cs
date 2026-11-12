using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The match reservation face driven as plain method calls. Every write is gated twice — by
/// the coordinator term that holds the partition lease, and (at commit) by the room ownership
/// epoch — so the rig seeds the real term with the coordinator store and the real candidate
/// chain with the identity, binding and ticket stores. Only the control plane speaks here.
/// </summary>
public sealed class RedesignMatchGrpcFaceTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";
    private const string Partition = "tenant-a:game-a:1:eu:simulator:ranked";
    private const string Holder = "control-1";
    private const string ReceiptJson = """{"tick":0,"hash":"aa"}""";

    [Fact]
    public async Task ReadyAnswersForTheCoordinator()
    {
        await using var rig = await MatchRig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.CoordinatorContext());
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheControlPlane()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("streamer-server", "ss-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task ReserveMatchHoldsTheCandidatesUnderTheTerm()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (host, hostBinding, hostSession) = await rig.MountAsync("live-key-1", "mount-1");
        var (guest, guestBinding, guestSession) = await rig.MountAsync("live-key-2", "mount-2");
        await rig.EnqueueAsync(hostBinding, hostSession, "enqueue-host-1");
        await rig.EnqueueAsync(guestBinding, guestSession);

        var reply = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(hostBinding, host), (guestBinding, guest)], term.Term),
            rig.CoordinatorContext());
        Assert.True(reply.Found);
        Assert.False(reply.Duplicate);
        Assert.Equal("Reserved", reply.Reservation.Status);
        Assert.Equal("room-m-1", reply.Reservation.RoomId);
        Assert.Equal(Tenant, reply.Reservation.TenantId);
        Assert.Equal(Game, reply.Reservation.GameId);
        Assert.Equal(Partition, reply.Reservation.PartitionKey);
        Assert.Equal(term.Term, reply.Reservation.CoordinatorTerm);
        Assert.True(reply.Reservation.ExpiresUnixMs > 0);
        Assert.Equal(2, reply.Reservation.Participants.Count);
        // Both tickets moved with the reservation and name it as their holder.
        var ticket = await rig.Tickets.GetTicketAsync(Tenant, Game, guestBinding);
        Assert.Equal("Reserved", ticket!.State);
        Assert.Equal(reply.Reservation.ReservationId, ticket.ReservationId);
    }

    [Fact]
    public async Task ReserveMatchAnswersDuplicateForAnExactReplay()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var coordinator = rig.CoordinatorContext();
        var request = Reserve("room-m-1", [(binding, streamer)], term.Term);
        var first = await rig.Service.ReserveMatch(request, coordinator);
        var reply = await rig.Service.ReserveMatch(request, coordinator);
        Assert.True(reply.Duplicate);
        Assert.Equal(first.Reservation.ReservationId, reply.Reservation.ReservationId);
    }

    [Fact]
    public async Task ReserveMatchRefusesAChangedCandidateSetUnderTheSameOperationId()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var coordinator = rig.CoordinatorContext();
        await rig.Service.ReserveMatch(Reserve("room-m-1", [(binding, streamer)], term.Term), coordinator);
        // The operation id is already spent: a retry cannot change what it reserved.
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(binding, "someone-else")], term.Term), coordinator),
            StatusCode.InvalidArgument, "The operation was retried with a different request.");
    }

    [Fact]
    public async Task ReserveMatchRefusesADisplacedTerm()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        // A takeover starts a new term; the old one never reserves again.
        await rig.ClaimTermAsync(holder: "control-2");
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(binding, streamer)], term.Term, holder: Holder),
                rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The term is no longer held by this coordinator.");
    }

    [Fact]
    public async Task ReserveMatchRefusesAMountWithoutATicket()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, _) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, $"The mount {binding} holds no candidate ticket.");
    }

    [Fact]
    public async Task ReserveMatchRefusesACandidateFromAnotherPartition()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, sessionId) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.Tickets.EnqueueTicketAsync(Tenant, Game, binding, sessionId,
            Partition + "/mobile", "enqueue-mobile-1");
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext()),
            StatusCode.FailedPrecondition,
            $"The ticket of {binding} belongs to partition {Partition}/mobile; " +
            "candidates from another partition never share a reservation.");
    }

    [Fact]
    public async Task ReserveMatchRefusesAnInactiveBinding()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        await rig.ExecuteAsync($"UPDATE live.binding SET state='Closing' WHERE binding_id='{binding}'");
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, $"The binding {binding} is no longer active.");
    }

    [Fact]
    public async Task ReserveMatchRefusesABindingOfAnotherScope()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, _) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(binding, streamer)], term.Term, gameId: "game-b"),
                rig.CoordinatorContext()),
            StatusCode.FailedPrecondition,
            $"The binding {binding} belongs to another tenant or game.");
    }

    [Fact]
    public async Task ReserveMatchRefusesAMountAlreadyInARound()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, _) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.ExecuteAsync($"""
            INSERT INTO game.room(room_id, tenant_id, game_id, status, epoch, package_hash,
                module_version, config_hash)
                VALUES ('room-live-1', 'tenant-a', 'game-a', 'Running', 1, 'pkg', 'v1', 'cfg');
            INSERT INTO game.participation(room_id, participant_id, binding_id, role, state)
                VALUES ('room-live-1', 'p-1', '{binding}', 'player', 'Active')
            """);
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, $"The binding {binding} already participates in a round.");
    }

    [Fact]
    public async Task ReserveMatchRefusesASecondReservationOfTheSameRoom()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (first, firstBinding, firstSession) = await rig.MountAsync("live-key-1", "mount-1");
        var (second, secondBinding, secondSession) = await rig.MountAsync("live-key-2", "mount-2");
        await rig.EnqueueAsync(firstBinding, firstSession);
        await rig.EnqueueAsync(secondBinding, secondSession);
        var coordinator = rig.CoordinatorContext();
        await rig.Service.ReserveMatch(Reserve("room-m-1", [(firstBinding, first)], term.Term), coordinator);
        // A different candidate set on the same room reaches the insert and meets the room's
        // unique reservation.
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(secondBinding, second)], term.Term, operationId: "op-match-2"),
                coordinator),
            StatusCode.FailedPrecondition, "The room is already reserved by another match.");
    }

    [Fact]
    public async Task ReserveMatchRefusesAnEmptyCandidateSet()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [], term.Term), rig.CoordinatorContext()),
            StatusCode.InvalidArgument,
            "A reservation needs at least one participant. (Parameter 'participants')");
    }

    [Fact]
    public async Task ReserveMatchRefusesAnOutOfRangeReservationWindow()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, _) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [(binding, streamer)], term.Term, seconds: 61), rig.CoordinatorContext()),
            StatusCode.InvalidArgument,
            "Specified argument was out of the range of valid values. (Parameter 'reservationSeconds')");
    }

    [Fact]
    public async Task ReserveMatchRefusesATenantScopedCallerActingForAnotherTenant()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var tenant = rig.Certificates.Context(rig.Certificates.Issue("tenant-a", "control-server", Holder));
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [], term.Term, tenantId: "tenant-b"), tenant),
            StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    [Fact]
    public async Task ReserveMatchRefusesARoleOutsideTheControlPlane()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [], 1), rig.Context("streamer-server", "ss-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task ReserveMatchHoldsAdmissionCapacityUntilItFrees()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        // A capped node counts the reservation itself against admission: a second candidate
        // set is rejected until the first one commits or retires.
        var capped = new MatchGrpcService(
            new RedesignMatchReservationStore(rig.Database.ConnectionString, maxActiveReservations: 1),
            rig.Certificates.Trusted);
        var (first, firstBinding, firstSession) = await rig.MountAsync("live-key-1", "mount-1");
        var (second, secondBinding, secondSession) = await rig.MountAsync("live-key-2", "mount-2");
        await rig.EnqueueAsync(firstBinding, firstSession);
        await rig.EnqueueAsync(secondBinding, secondSession);
        var coordinator = rig.CoordinatorContext();
        await capped.ReserveMatch(Reserve("room-c-1", [(firstBinding, first)], term.Term), coordinator);
        await rig.Rejects(() => capped.ReserveMatch(
                Reserve("room-c-2", [(secondBinding, second)], term.Term, operationId: "op-match-2"),
                coordinator),
            StatusCode.ResourceExhausted, "The node is at its active room capacity.");
    }

    [Fact]
    public async Task RenewReservationExtendsTheDeadline()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        var reply = await rig.Service.RenewReservation(new RenewReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, ReservationSeconds = 45,
            OperationId = "op-renew-1"
        }, rig.CoordinatorContext());
        Assert.True(reply.Found);
        Assert.False(reply.Duplicate);
        Assert.Equal("Reserved", reply.Reservation.Status);
        Assert.True(reply.Reservation.ExpiresUnixMs > reserved.Reservation.ExpiresUnixMs);
    }

    [Fact]
    public async Task RenewReservationRefusesADisplacedTerm()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.ClaimTermAsync(holder: "control-2");
        await rig.Rejects(() => rig.Service.RenewReservation(new RenewReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, ReservationSeconds = 45,
            OperationId = "op-renew-1"
        }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The term is no longer held by this coordinator.");
    }

    [Fact]
    public async Task RenewReservationRefusesAnExpiredReservation()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        // An expired reservation cannot be revived: the sweep owns it.
        await rig.ExpireReservationAsync("room-m-1");
        await rig.Rejects(() => rig.Service.RenewReservation(new RenewReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, ReservationSeconds = 45,
            OperationId = "op-renew-1"
        }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The reservation is not active or its deadline already passed.");
    }

    [Fact]
    public async Task MarkPreparedRecordsThePrepareReceipt()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.SeedOwnershipAsync("room-m-1");
        var reply = await rig.Service.MarkPrepared(new MarkPreparedRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, PreparedReceiptJson = ReceiptJson,
            OperationId = "op-prepare-1"
        }, rig.CoordinatorContext());
        Assert.True(reply.Found);
        Assert.Equal("Prepared", reply.Reservation.Status);
        // The receipt is stored as jsonb and reads back normalized: compared as JSON.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ReceiptJson),
            JsonNode.Parse(reply.Reservation.PreparedReceiptJson)));
    }

    [Fact]
    public async Task MarkPreparedRefusesARoomWithoutHeldOwnership()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.Rejects(() => rig.Service.MarkPrepared(new MarkPreparedRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, PreparedReceiptJson = ReceiptJson,
            OperationId = "op-prepare-1"
        }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The room ownership is not held for the reserved room.");
    }

    [Fact]
    public async Task MarkPreparedRefusesADisplacedTerm()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.SeedOwnershipAsync("room-m-1");
        await rig.ClaimTermAsync(holder: "control-2");
        await rig.Rejects(() => rig.Service.MarkPrepared(new MarkPreparedRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, PreparedReceiptJson = ReceiptJson,
            OperationId = "op-prepare-1"
        }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The term is no longer held by this coordinator.");
    }

    [Fact]
    public async Task CommitMatchCommitsTheRound()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.SeedOwnershipAsync("room-m-1", ownerInstanceId: "room-host-1", epoch: 2);
        await rig.Service.MarkPrepared(new MarkPreparedRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, PreparedReceiptJson = ReceiptJson,
            OperationId = "op-prepare-1"
        }, rig.CoordinatorContext());
        var reply = await rig.Service.CommitMatch(new CommitMatchRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder,
            RoomInstanceId = "room-host-1", RoomEpoch = 2, OperationId = "op-commit-1"
        }, rig.CoordinatorContext());
        Assert.True(reply.Found);
        Assert.False(reply.Duplicate);
        Assert.Equal("Committed", reply.Reservation.Status);
        // The tickets leave the active slots with the reservation: everyone commits together.
        var ticket = await rig.Tickets.GetTicketAsync(Tenant, Game, binding);
        Assert.Equal("Committed", ticket!.State);
    }

    [Fact]
    public async Task CommitMatchRefusesAClaimByADisplacedOwner()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.SeedOwnershipAsync("room-m-1", ownerInstanceId: "room-host-1", epoch: 2);
        // A write carrying a displaced room owner's instance never commits.
        await rig.Rejects(() => rig.Service.CommitMatch(new CommitMatchRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder,
            RoomInstanceId = "room-host-9", RoomEpoch = 2, OperationId = "op-commit-1"
        }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The room is fenced by another owner.");
    }

    [Fact]
    public async Task CommitMatchRefusesADisplacedTerm()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.SeedOwnershipAsync("room-m-1", ownerInstanceId: "room-host-1", epoch: 2);
        await rig.ClaimTermAsync(holder: "control-2");
        await rig.Rejects(() => rig.Service.CommitMatch(new CommitMatchRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder,
            RoomInstanceId = "room-host-1", RoomEpoch = 2, OperationId = "op-commit-1"
        }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The term is no longer held by this coordinator.");
    }

    [Fact]
    public async Task CommitMatchRefusesAnAlreadyRetiredReservation()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.Service.AbortReservation(new AbortReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, OperationId = "op-abort-1"
        }, rig.CoordinatorContext());
        // The ownership fence is checked before the reservation status, so the claim carries
        // a held room: the refusal is then the retired state, not a displaced owner.
        await rig.SeedOwnershipAsync("room-m-1", ownerInstanceId: "room-host-1", epoch: 2);
        await rig.Rejects(() => rig.Service.CommitMatch(new CommitMatchRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder,
            RoomInstanceId = "room-host-1", RoomEpoch = 2, OperationId = "op-commit-1"
        }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The reservation is already committed or retired.");
    }

    [Fact]
    public async Task AbortReservationReleasesTheCandidates()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        var reply = await rig.Service.AbortReservation(new AbortReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, OperationId = "op-abort-1"
        }, rig.CoordinatorContext());
        Assert.True(reply.Found);
        Assert.Equal("Aborted", reply.Reservation.Status);
        // The tickets return to the queue: capacity never leaks through an abort.
        var ticket = await rig.Tickets.GetTicketAsync(Tenant, Game, binding);
        Assert.Equal("Queued", ticket!.State);
    }

    [Fact]
    public async Task AbortReservationRefusesACommittedReservation()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.SeedOwnershipAsync("room-m-1", ownerInstanceId: "room-host-1", epoch: 2);
        await rig.Service.MarkPrepared(new MarkPreparedRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, PreparedReceiptJson = ReceiptJson,
            OperationId = "op-prepare-1"
        }, rig.CoordinatorContext());
        await rig.Service.CommitMatch(new CommitMatchRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder,
            RoomInstanceId = "room-host-1", RoomEpoch = 2, OperationId = "op-commit-1"
        }, rig.CoordinatorContext());
        // A committed reservation belongs to its round and never aborts.
        await rig.Rejects(() => rig.Service.AbortReservation(new AbortReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, OperationId = "op-abort-1"
        }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The reservation is already committed or retired.");
    }

    [Fact]
    public async Task GetReservationAnswersTheSnapshot()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        var reply = await rig.Service.GetReservation(new GetReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId
        }, rig.CoordinatorContext());
        Assert.True(reply.Found);
        Assert.Equal("room-m-1", reply.Reservation.RoomId);
        Assert.Equal("Reserved", reply.Reservation.Status);
    }

    [Fact]
    public async Task GetReservationAnswersNotFoundWithoutAReservation()
    {
        await using var rig = await MatchRig.CreateAsync();
        var reply = await rig.Service.GetReservation(new GetReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = "reservation-none"
        }, rig.CoordinatorContext());
        Assert.False(reply.Found);
        Assert.Null(reply.Reservation);
    }

    [Fact]
    public async Task ExpireReservationsRetiresPassedDeadlines()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.ExpireReservationAsync("room-m-1");
        var reply = await rig.Service.ExpireReservations(new ExpireReservationsRequest { Max = 10 },
            rig.CoordinatorContext());
        Assert.Equal(1, reply.Expired);
        var snapshot = await rig.Service.GetReservation(new GetReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = reserved.Reservation.ReservationId
        }, rig.CoordinatorContext());
        Assert.Equal("Expired", snapshot.Reservation.Status);
        // The candidates return to the queue unless their own expiry passed.
        var ticket = await rig.Tickets.GetTicketAsync(Tenant, Game, binding);
        Assert.Equal("Queued", ticket!.State);
        var again = await rig.Service.ExpireReservations(new ExpireReservationsRequest { Max = 10 },
            rig.CoordinatorContext());
        Assert.Equal(0, again.Expired);
    }

    [Fact]
    public async Task ExpireReservationsRefusesAnOutOfWindowMax()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ExpireReservations(new ExpireReservationsRequest { Max = 0 },
                rig.CoordinatorContext()),
            StatusCode.InvalidArgument, "The sweep window is 1..100 reservations. (Parameter 'max')");
    }

    [Fact]
    public async Task ListActiveReservationsAnswersTheRecoveryScan()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (first, firstBinding, firstSession) = await rig.MountAsync("live-key-1", "mount-1");
        var (second, secondBinding, secondSession) = await rig.MountAsync("live-key-2", "mount-2");
        await rig.EnqueueAsync(firstBinding, firstSession);
        await rig.EnqueueAsync(secondBinding, secondSession);
        var coordinator = rig.CoordinatorContext();
        await rig.Service.ReserveMatch(Reserve("room-m-1", [(firstBinding, first)], term.Term), coordinator);
        var retired = await rig.Service.ReserveMatch(
            Reserve("room-m-2", [(secondBinding, second)], term.Term, operationId: "op-match-2"), coordinator);
        await rig.Service.AbortReservation(new AbortReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = retired.Reservation.ReservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = Holder, OperationId = "op-abort-1"
        }, coordinator);
        // Terminal reservations are history and never re-driven by the successor scan.
        var reply = await rig.Service.ListActiveReservations(
            new ListActiveReservationsRequest { PartitionKey = Partition, Max = 10 }, coordinator);
        var reservation = Assert.Single(reply.Reservations);
        Assert.Equal("room-m-1", reservation.RoomId);
        Assert.Equal("Reserved", reservation.Status);
    }

    [Fact]
    public async Task ListActiveReservationsRefusesAnOutOfWindowMax()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ListActiveReservations(
                new ListActiveReservationsRequest { PartitionKey = Partition, Max = 0 }, rig.CoordinatorContext()),
            StatusCode.InvalidArgument, "The recovery window is 1..500 reservations. (Parameter 'max')");
    }

    [Fact]
    public async Task ListReservationPartitionsAnswersThePartitionsWithActiveReservations()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var otherTerm = await rig.ClaimTermAsync(Partition + "/mobile");
        var (first, firstBinding, firstSession) = await rig.MountAsync("live-key-1", "mount-1");
        var (second, secondBinding, sessionId) = await rig.MountAsync("live-key-2", "mount-2");
        await rig.EnqueueAsync(firstBinding, firstSession);
        await rig.Tickets.EnqueueTicketAsync(Tenant, Game, secondBinding, sessionId,
            Partition + "/mobile", "enqueue-mobile-1");
        var coordinator = rig.CoordinatorContext();
        await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(firstBinding, first)], term.Term), coordinator);
        var retired = await rig.Service.ReserveMatch(
            Reserve("room-m-2", [(secondBinding, second)], otherTerm.Term, partition: Partition + "/mobile",
                operationId: "op-match-2"), coordinator);
        await rig.Service.AbortReservation(new AbortReservationRequest
        {
            TenantId = Tenant, GameId = Game, ReservationId = retired.Reservation.ReservationId,
            CoordinatorTerm = otherTerm.Term, HolderInstanceId = Holder, OperationId = "op-abort-1"
        }, coordinator);
        // An orphaned reservation can be the only living thing in its partition; a retired
        // one is not.
        var reply = await rig.Service.ListReservationPartitions(new Empty(), coordinator);
        Assert.Equal([Partition], reply.PartitionKeys);
    }

    [Fact]
    public async Task RenewReservationRefusesABlankOperationId()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.RenewReservation(new RenewReservationRequest
            {
                TenantId = Tenant, GameId = Game, ReservationId = "res-1",
                CoordinatorTerm = 1, HolderInstanceId = Holder, OperationId = ""
            }, rig.CoordinatorContext()),
            StatusCode.InvalidArgument, detailContains: "Value is required.");
    }

    [Fact]
    public async Task MarkPreparedRefusesABlankReceipt()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.MarkPrepared(new MarkPreparedRequest
            {
                TenantId = Tenant, GameId = Game, ReservationId = "res-1",
                CoordinatorTerm = 1, HolderInstanceId = Holder, PreparedReceiptJson = "",
                OperationId = "op-prepare-1"
            }, rig.CoordinatorContext()),
            StatusCode.InvalidArgument, detailContains: "Value is required.");
    }

    [Fact]
    public async Task CommitMatchRefusesABlankRoomInstance()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.CommitMatch(new CommitMatchRequest
            {
                TenantId = Tenant, GameId = Game, ReservationId = "res-1",
                CoordinatorTerm = 1, HolderInstanceId = Holder,
                RoomInstanceId = "", RoomEpoch = 1, OperationId = "op-commit-1"
            }, rig.CoordinatorContext()),
            StatusCode.InvalidArgument, detailContains: "Value is required.");
    }

    [Fact]
    public async Task AbortReservationRefusesABlankOperationId()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.AbortReservation(new AbortReservationRequest
            {
                TenantId = Tenant, GameId = Game, ReservationId = "res-1",
                CoordinatorTerm = 1, HolderInstanceId = Holder, OperationId = ""
            }, rig.CoordinatorContext()),
            StatusCode.InvalidArgument, detailContains: "Value is required.");
    }

    [Fact]
    public async Task GetReservationRefusesABlankReservationId()
    {
        await using var rig = await MatchRig.CreateAsync();
        await rig.Rejects(() => rig.Service.GetReservation(new GetReservationRequest
            { TenantId = Tenant, GameId = Game, ReservationId = "" }, rig.CoordinatorContext()),
            StatusCode.InvalidArgument, detailContains: "Value is required.");
    }

    [Fact]
    public async Task AbortReservationRefusesATermItDoesNotHold()
    {
        await using var rig = await MatchRig.CreateAsync();
        var term = await rig.ClaimTermAsync();
        var (streamer, binding, session) = await rig.MountAsync("live-key-1", "mount-1");
        await rig.EnqueueAsync(binding, session);
        var reserved = await rig.Service.ReserveMatch(
            Reserve("room-m-1", [(binding, streamer)], term.Term), rig.CoordinatorContext());
        await rig.Rejects(() => rig.Service.AbortReservation(new AbortReservationRequest
            {
                TenantId = Tenant, GameId = Game,
                ReservationId = reserved.Reservation.ReservationId,
                CoordinatorTerm = term.Term + 1, HolderInstanceId = Holder,
                OperationId = "op-abort-1"
            }, rig.CoordinatorContext()),
            StatusCode.FailedPrecondition, "The term is no longer held by this coordinator.");
    }

    [Fact]
    public async Task AMatchTenantCallerCannotActForAnotherTenant()
    {
        await using var rig = await MatchRig.CreateAsync();
        var context = rig.Certificates.Context(rig.Certificates.Issue(Tenant, "control-server", Holder));
        await rig.Rejects(() => rig.Service.ReserveMatch(
                Reserve("room-m-1", [], 1, tenantId: "tenant-b"), context),
            StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    private static ReserveMatchRequest Reserve(string roomId,
        IReadOnlyList<(string Binding, string Participant)> candidates, long term,
        int seconds = 30, string operationId = "op-match-1", string tenantId = Tenant,
        string gameId = Game, string partition = Partition, string holder = Holder)
    {
        var request = new ReserveMatchRequest
        {
            TenantId = tenantId, GameId = gameId, PartitionKey = partition, CoordinatorTerm = term,
            HolderInstanceId = holder, RoomId = roomId, ReservationSeconds = seconds,
            OperationId = operationId
        };
        foreach (var (binding, participant) in candidates)
            request.Participants.Add(new ReservedParticipantProto
                { BindingId = binding, ParticipantId = participant, Role = "Host" });
        return request;
    }

    private sealed record MatchRig(MatchGrpcService Service, RedesignMatchTicketStore Tickets,
        RedesignIdentityStore Identity, RedesignBindingStore Bindings,
        RedesignCoordinatorStore Coordinator, TestProcessCertificates Certificates,
        TemporaryDatabase Database) : IAsyncDisposable
    {
        public static async Task<MatchRig> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            var certificates = new TestProcessCertificates();
            var connection = database.ConnectionString;
            var identity = new RedesignIdentityStore(connection);
            var bindings = new RedesignBindingStore(connection);
            var coordinator = new RedesignCoordinatorStore(connection);
            var tickets = new RedesignMatchTicketStore(connection);
            var service = new MatchGrpcService(new RedesignMatchReservationStore(connection),
                certificates.Trusted);
            return new MatchRig(service, tickets, identity, bindings, coordinator, certificates, database);
        }

        /// <summary>Mounts a real candidate chain: identity, live session, login session and
        /// gameplay binding — the rows the reservation walk locks in the unified order.</summary>
        public async Task<(string StreamerId, string BindingId, string SessionId)> MountAsync(
            string canonicalKey, string openId)
        {
            var streamer = await Identity.ResolveIdentityAsync("tenant-a", "simulator", "app-1", openId);
            var live = await Bindings.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
                "app-1", "platform-room", canonicalKey);
            var session = await Identity.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
                RandomNumberGenerator.GetBytes(32));
            var binding = await Bindings.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a",
                "op-" + openId);
            return (streamer.StreamerId, binding.BindingId, session.SessionId);
        }

        public async Task EnqueueAsync(string bindingId, string sessionId,
            string operationId = "enqueue-op-1")
        {
            await Tickets.EnqueueTicketAsync(Tenant, Game, bindingId, sessionId,
                Partition, operationId);
        }

        public async Task<TermStatus> ClaimTermAsync(string partition = Partition,
            string holder = Holder) => await Coordinator.ClaimTermAsync(partition, holder, 60);

        public async Task SeedOwnershipAsync(string roomId, string ownerInstanceId = "room-host-1",
            long epoch = 2)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.ownership(kind, entity_id, owner_process_instance_id, epoch,
                    lease_until, status, version, tenant_id, game_id)
                VALUES ('room', @room, @owner, @epoch,
                    clock_timestamp() + interval '5 minutes', 'Active', 1, 'tenant-a', 'game-a')
                """, connection);
            seed.Parameters.AddWithValue("room", roomId);
            seed.Parameters.AddWithValue("owner", ownerInstanceId);
            seed.Parameters.AddWithValue("epoch", epoch);
            await seed.ExecuteNonQueryAsync();
        }

        public async Task ExpireReservationAsync(string roomId)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE match.reservation SET expires_at=clock_timestamp()-interval '1 second' WHERE room_id=@room",
                connection);
            command.Parameters.AddWithValue("room", roomId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public TestServerCallContext Context(string role, string instance) =>
            Certificates.Context(Certificates.Issue("@cluster", role, instance));

        public TestServerCallContext CoordinatorContext() =>
            Certificates.Context(Certificates.Issue("@cluster", "control-server", Holder));

        public async Task Rejects(Func<Task> call, StatusCode code, string? detail = null,
            string? detailContains = null)
        {
            var error = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal(code, error.Status.StatusCode);
            if (detail is not null) Assert.Equal(detail, error.Status.Detail);
            if (detailContains is not null) Assert.Contains(detailContains, error.Status.Detail);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_match_face_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            // DROP ... WITH (FORCE) collects the database's backends under one lock and
            // checks each pid's privileges after releasing it: a backend that exits in
            // between can have its OS pid reused by another role's connection under a
            // co-tenant fork storm, and the privilege check then lands on that stranger
            // and fails with 42501. The exiting backend is really gone within
            // milliseconds, so a short bounded retry settles it.
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
