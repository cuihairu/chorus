using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Chorus.Data.Redesign;
using Chorus.ControlServer;
using Chorus.DataServer;
using Chorus.Redesign.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Match reservation store (M3-2, architecture 7.2/10.1): the term gates every write, the
/// whole candidate set reserves atomically under the shared admission cap, prepare needs the
/// room ownership, and the expiry sweep retires a reservation with everything it held —
/// tickets, room, ownership — so capacity never leaks (AT11).
/// </summary>
public sealed class RedesignMatchReservationStoreTests
{
    private const string Partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";

    [Fact]
    public async Task ReserveValidatesTheTermAndTheCandidateSetAndIsIdempotent()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);
        var reservations = new RedesignMatchReservationStore(database.ConnectionString);

        var term = await coordinator.ClaimTermAsync(Partition, "control-a", 60);
        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-p1", "reserve-p1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-p2", "reserve-p2");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-1");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b2.BindingId, b2.SessionId,
            Partition, "enqueue-op-2");

        var reservation = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-1",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            30, "reserve-op-1");
        Assert.Equal(RedesignMatchReservationStore.Reserved, reservation.Status);
        Assert.Equal("room-m-1", reservation.RoomId);
        Assert.False(reservation.Duplicate);
        Assert.Equal(2, reservation.Participants.Count);
        Assert.Contains(reservation.Participants, p => p.BindingId == b1.BindingId);
        Assert.Contains(reservation.Participants, p => p.BindingId == b2.BindingId);

        // Both tickets moved with the reservation; an exact retry replays it.
        var ticket1 = await tickets.GetTicketAsync("tenant-a", "game-a", b1.BindingId);
        Assert.Equal("Reserved", ticket1!.State);
        Assert.Equal(reservation.ReservationId, ticket1.ReservationId);
        var retry = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-1",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            30, "reserve-op-1");
        Assert.True(retry.Duplicate);
        Assert.Equal(reservation.ReservationId, retry.ReservationId);
        // The same room and operation with a different candidate set conflicts.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => reservations.ReserveAsync(
            "tenant-a", "game-a", Partition, term.Term, "control-a", "room-m-1",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host")], 30, "reserve-op-1"));

        // A takeover starts a new term; the old one never reserves again (AT10).
        var takeover = await coordinator.ClaimTermAsync(Partition, "control-b", 60);
        Assert.True(takeover.Term > term.Term);
        await Assert.ThrowsAsync<RedesignStaleTermException>(() => reservations.ReserveAsync(
            "tenant-a", "game-a", Partition, term.Term, "control-a", "room-m-3",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host")], 30, "reserve-op-2"));
        await Assert.ThrowsAsync<RedesignStaleTermException>(() => reservations.ReserveAsync(
            "tenant-a", "game-a", Partition, takeover.Term, "control-a", "room-m-3",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host")], 30, "reserve-op-3"));

        // The candidate set must be queued tickets of this partition: a candidate from another
        // partition never shares a reservation, and a mount without a ticket never reserves.
        var (p3, b3) = await MountAsync(identityStore, bindingStore, "live-key-p3", "reserve-p3");
        var (p4, b4) = await MountAsync(identityStore, bindingStore, "live-key-p4", "reserve-p4");
        var (_, b5) = await MountAsync(identityStore, bindingStore, "live-key-p5", "reserve-p5");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b3.BindingId, b3.SessionId,
            Partition, "enqueue-op-3");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b4.BindingId, b4.SessionId,
            Partition + "/mobile", "enqueue-op-4");
        await Assert.ThrowsAsync<RedesignMatchException>(() => reservations.ReserveAsync(
            "tenant-a", "game-a", Partition, takeover.Term, "control-b", "room-m-4",
            [new ReservedParticipant(b3.BindingId, p3.StreamerId, "Host"),
             new ReservedParticipant(b4.BindingId, p4.StreamerId, "Guest")], 30, "reserve-op-4"));
        await Assert.ThrowsAsync<RedesignMatchException>(() => reservations.ReserveAsync(
            "tenant-a", "game-a", Partition, takeover.Term, "control-b", "room-m-4",
            [new ReservedParticipant(b3.BindingId, p3.StreamerId, "Host"),
             new ReservedParticipant(b5.BindingId, "streamer-x", "Guest")], 30, "reserve-op-5"));

        var snapshot = await reservations.GetReservationAsync("tenant-a", "game-a",
            reservation.ReservationId);
        Assert.NotNull(snapshot);
        Assert.Equal("simulator", snapshot!.PartitionKey.Split('/')[4]);
        Assert.Null(await reservations.GetReservationAsync("tenant-a", "game-a", "reservation-none"));
    }

    [Fact]
    public async Task ReserveSharesTheAdmissionCapWithRoomsAndNeverLeaksIt()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);
        var reservations = new RedesignMatchReservationStore(database.ConnectionString,
            maxActiveReservations: 1);

        var term = await coordinator.ClaimTermAsync(Partition, "control-a", 60);
        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-c1", "reserve-c1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-c2", "reserve-c2");
        var (p3, b3) = await MountAsync(identityStore, bindingStore, "live-key-c3", "reserve-c3");
        var (p4, b4) = await MountAsync(identityStore, bindingStore, "live-key-c4", "reserve-c4");
        foreach (var (binding, op) in new[] { (b1, "enqueue-op-1"), (b2, "enqueue-op-2"),
                                              (b3, "enqueue-op-3"), (b4, "enqueue-op-4") })
            await tickets.EnqueueTicketAsync("tenant-a", "game-a", binding.BindingId, binding.SessionId,
                Partition, op);

        await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term, "control-a",
            "room-m-1", [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
                         new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            30, "reserve-op-1");
        await Assert.ThrowsAsync<RedesignCapacityException>(() => reservations.ReserveAsync(
            "tenant-a", "game-a", Partition, term.Term, "control-a", "room-m-2",
            [new ReservedParticipant(b3.BindingId, p3.StreamerId, "Host"),
             new ReservedParticipant(b4.BindingId, p4.StreamerId, "Guest")], 30, "reserve-op-2"));

        // The refusal is counted, then the sweep retires the expired reservation and the slot
        // is free again — capacity never leaks (AT11).
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString,
            "SELECT count FROM ops.capacity_rejection WHERE kind='reserve-match:active-rooms'"));
        await ShiftReservationExpiryAsync(database.ConnectionString, "room-m-1");
        Assert.Equal(1, await reservations.ExpireReservationsAsync(10));
        var reserved = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-2",
            [new ReservedParticipant(b3.BindingId, p3.StreamerId, "Host"),
             new ReservedParticipant(b4.BindingId, p4.StreamerId, "Guest")],
            30, "reserve-op-3");
        Assert.Equal(RedesignMatchReservationStore.Reserved, reserved.Status);
    }

    [Fact]
    public async Task PrepareAndRenewGuardOwnershipTermAndDeadline()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);
        var reservations = new RedesignMatchReservationStore(database.ConnectionString);

        var term = await coordinator.ClaimTermAsync(Partition, "control-a", 60);
        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-g1", "reserve-g1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-g2", "reserve-g2");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-1");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b2.BindingId, b2.SessionId,
            Partition, "enqueue-op-2");
        var reservation = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-1",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            30, "reserve-op-1");

        // Without the room ownership the prepare receipt is refused.
        await Assert.ThrowsAsync<RedesignMatchException>(() => reservations.MarkPreparedAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term, "control-a",
            """{"tick":0,"state":"ready"}""", "prepare-op-1"));
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-m-1", "gs-1", 30);
        Assert.Equal(1L, claim.Epoch);
        var prepared = await reservations.MarkPreparedAsync("tenant-a", "game-a",
            reservation.ReservationId, term.Term, "control-a",
            """{"tick":0,"state":"ready"}""", "prepare-op-1");
        Assert.Equal(RedesignMatchReservationStore.Prepared, prepared.Status);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"tick":0,"state":"ready"}"""),
            JsonNode.Parse(prepared.PreparedReceiptJson)));
        var retry = await reservations.MarkPreparedAsync("tenant-a", "game-a",
            reservation.ReservationId, term.Term, "control-a",
            """{"tick":0,"state":"ready"}""", "prepare-op-1");
        Assert.True(retry.Duplicate);

        // Renewal extends the deadline — and an expired reservation belongs to the sweep.
        var renewed = await reservations.RenewReservationAsync("tenant-a", "game-a",
            reservation.ReservationId, term.Term, "control-a", 60, "renew-op-1");
        Assert.True(renewed.ExpiresAtUtc > reservation.ExpiresAtUtc);
        await ShiftReservationExpiryAsync(database.ConnectionString, "room-m-1");
        await Assert.ThrowsAsync<RedesignMatchException>(() => reservations.RenewReservationAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term, "control-a", 30, "renew-op-2"));
        await Assert.ThrowsAsync<RedesignMatchException>(() => reservations.MarkPreparedAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term, "control-a",
            """{"tick":1}""", "prepare-op-2"));

        // A stale term never prepares or renews, even on a live reservation.
        var (p3, b3) = await MountAsync(identityStore, bindingStore, "live-key-g3", "reserve-g3");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b3.BindingId, b3.SessionId,
            Partition, "enqueue-op-3");
        var live = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-2",
            [new ReservedParticipant(b3.BindingId, p3.StreamerId, "Host")], 30, "reserve-op-2");
        var takeover = await coordinator.ClaimTermAsync(Partition, "control-b", 60);
        await Assert.ThrowsAsync<RedesignStaleTermException>(() => reservations.MarkPreparedAsync(
            "tenant-a", "game-a", live.ReservationId, term.Term, "control-a",
            """{"tick":0}""", "prepare-op-3"));
        await Assert.ThrowsAsync<RedesignStaleTermException>(() => reservations.RenewReservationAsync(
            "tenant-a", "game-a", live.ReservationId, term.Term, "control-a", 30, "renew-op-3"));
    }

    [Fact]
    public async Task ExpiryRetiresTheReservationAndReleasesEverythingItHeld()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);
        var reservations = new RedesignMatchReservationStore(database.ConnectionString);

        var term = await coordinator.ClaimTermAsync(Partition, "control-a", 60);
        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-e1", "reserve-e1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-e2", "reserve-e2");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-1", DateTime.UtcNow.AddHours(1));
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b2.BindingId, b2.SessionId,
            Partition, "enqueue-op-2");
        var reservation = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-1",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            30, "reserve-op-1");

        // The started-room window: ownership claimed, the round started, pointers moved. The
        // coordinator dies here; the sweep must release all of it (AT11). The host ticket's own
        // deadline passes inside the window, so it retires with the reservation instead of
        // returning to the queue.
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-m-1", "gs-1", 30);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-m-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new RoundParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            "1.0.0", "cfg-1", "pkg-1", "room-op-1");
        await ExecuteAsync(database.ConnectionString,
            "UPDATE match.ticket SET expires_at=clock_timestamp()-interval '1 second' WHERE binding_id=@binding",
            ("binding", b1.BindingId));

        await ShiftReservationExpiryAsync(database.ConnectionString, "room-m-1");
        Assert.Equal(1, await reservations.ExpireReservationsAsync(10));
        Assert.Equal(0, await reservations.ExpireReservationsAsync(10));

        var retired = await reservations.GetReservationAsync("tenant-a", "game-a",
            reservation.ReservationId);
        Assert.Equal(RedesignMatchReservationStore.Expired, retired!.Status);
        Assert.Equal("Aborted", await ScalarAsync(database.ConnectionString,
            "SELECT status FROM game.room WHERE room_id='room-m-1'"));
        Assert.Equal(0L, await ScalarAsync(database.ConnectionString,
            "SELECT count(*) FROM game.participation WHERE room_id='room-m-1' AND state IN ('Active','Offline')"));
        Assert.Equal(0L, await ScalarAsync(database.ConnectionString,
            "SELECT count(*) FROM live.binding WHERE binding_id=ANY(@bindings) AND current_room_id='room-m-1'",
            ("bindings", new[] { b1.BindingId, b2.BindingId })));
        Assert.Equal("Released", await ScalarAsync(database.ConnectionString,
            "SELECT status FROM control.ownership WHERE kind='room' AND entity_id='room-m-1'"));
        Assert.Equal(claim.Epoch + 1, await ScalarAsync(database.ConnectionString,
            "SELECT epoch FROM control.ownership WHERE kind='room' AND entity_id='room-m-1'"));

        // The self-expired ticket retires with the reservation; the other returns to the queue.
        Assert.Equal(RedesignMatchTicketStore.Expired,
            (await tickets.GetTicketAsync("tenant-a", "game-a", b1.BindingId))!.State);
        var returned = await tickets.GetTicketAsync("tenant-a", "game-a", b2.BindingId);
        Assert.Equal(RedesignMatchTicketStore.Queued, returned!.State);
        Assert.Null(returned.ReservationId);

        // Both effects are in the outbox exactly once.
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString,
            "SELECT count(*) FROM infra.outbox WHERE effect_id='room-m-1/0/abort' AND kind='round.aborted'"));
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString,
            "SELECT count(*) FROM infra.outbox WHERE effect_id=@effect AND kind='match.expired'",
            ("effect", $"{reservation.ReservationId}/expire")));
    }

    [Fact]
    public async Task CommitIsFencedByTermOwnershipAndDeadline()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);
        var reservations = new RedesignMatchReservationStore(database.ConnectionString);

        var term = await coordinator.ClaimTermAsync(Partition, "control-a", 60);
        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-f1", "reserve-f1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-f2", "reserve-f2");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-1");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b2.BindingId, b2.SessionId,
            Partition, "enqueue-op-2");
        var reservation = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-1",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            30, "reserve-op-1");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-m-1", "gs-1", 30);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-m-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new RoundParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            "1.0.0", "cfg-1", "pkg-1", "room-op-1");
        await reservations.MarkPreparedAsync("tenant-a", "game-a", reservation.ReservationId,
            term.Term, "control-a", """{"tick":0,"state":"ready"}""", "prepare-op-1");

        // A stale term, a displaced room instance and a wrong epoch never commit (AT10/AT25
        // mechanics); the fence is the ownership row, not a claim in memory.
        await Assert.ThrowsAsync<RedesignStaleTermException>(() => reservations.CommitMatchAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term + 1, "control-a",
            "gs-1", claim.Epoch, "commit-op-x"));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => reservations.CommitMatchAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term, "control-a",
            "gs-2", claim.Epoch, "commit-op-x"));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => reservations.CommitMatchAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term, "control-a",
            "gs-1", claim.Epoch + 5, "commit-op-x"));

        var committed = await reservations.CommitMatchAsync("tenant-a", "game-a",
            reservation.ReservationId, term.Term, "control-a", "gs-1", claim.Epoch, "commit-op-1");
        Assert.Equal(RedesignMatchReservationStore.Committed, committed.Status);
        Assert.Equal("Committed", (await tickets.GetTicketAsync("tenant-a", "game-a", b1.BindingId))!.State);
        Assert.Equal("Committed", (await tickets.GetTicketAsync("tenant-a", "game-a", b2.BindingId))!.State);

        // The retry replays; a second operation on a committed reservation refuses visibly.
        var retry = await reservations.CommitMatchAsync("tenant-a", "game-a",
            reservation.ReservationId, term.Term, "control-a", "gs-1", claim.Epoch, "commit-op-1");
        Assert.True(retry.Duplicate);
        await Assert.ThrowsAsync<RedesignMatchException>(() => reservations.CommitMatchAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term, "control-a",
            "gs-1", claim.Epoch, "commit-op-2"));

        // The committed tickets left the active slots — but the mounts now participate in the
        // live round, so they refuse to queue again until it ends.
        await Assert.ThrowsAsync<RedesignMatchException>(() => tickets.EnqueueTicketAsync(
            "tenant-a", "game-a", b1.BindingId, b1.SessionId, Partition, "enqueue-op-3"));

        // An expired reservation is owned by the sweep — commit refuses even with the fence held.
        var (p3, b3) = await MountAsync(identityStore, bindingStore, "live-key-f3", "reserve-f3");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b3.BindingId, b3.SessionId,
            Partition, "enqueue-op-4");
        var late = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-2",
            [new ReservedParticipant(b3.BindingId, p3.StreamerId, "Host")], 30, "reserve-op-2");
        var lateClaim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-m-2",
            "gs-1", 30);
        await ShiftReservationExpiryAsync(database.ConnectionString, "room-m-2");
        await Assert.ThrowsAsync<RedesignMatchException>(() => reservations.CommitMatchAsync(
            "tenant-a", "game-a", late.ReservationId, term.Term, "control-a", "gs-1", lateClaim.Epoch,
            "commit-op-3"));
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString,
            $"SELECT count(*) FROM infra.outbox WHERE effect_id='{reservation.ReservationId}/commit' AND kind='match.committed'"));
    }

    [Fact]
    public async Task ASuccessorCommitsAPreparedReservationWithoutReReserving()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);
        var reservations = new RedesignMatchReservationStore(database.ConnectionString);

        // The coordinator dies after prepare: the successor reads the frozen reservation with
        // its prepare receipt and commits the SAME room — it never reserves a second one (AT11).
        var term = await coordinator.ClaimTermAsync(Partition, "control-a", 60);
        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-s5", "reserve-s5");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-s6", "reserve-s6");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-1");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b2.BindingId, b2.SessionId,
            Partition, "enqueue-op-2");
        var reservation = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-1",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            60, "reserve-op-1");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-m-1", "gs-1", 60);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-m-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new RoundParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            "1.0.0", "cfg-1", "pkg-1", "room-op-1");
        await reservations.MarkPreparedAsync("tenant-a", "game-a", reservation.ReservationId,
            term.Term, "control-a", """{"tick":0,"state":"ready"}""", "prepare-op-1");

        var takeover = await coordinator.ClaimTermAsync(Partition, "control-b", 60);
        var found = await reservations.GetReservationAsync("tenant-a", "game-a",
            reservation.ReservationId);
        Assert.Equal(RedesignMatchReservationStore.Prepared, found!.Status);
        Assert.NotNull(found.PreparedReceiptJson);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"tick":0,"state":"ready"}"""),
            JsonNode.Parse(found.PreparedReceiptJson)));

        var committed = await reservations.CommitMatchAsync("tenant-a", "game-a",
            reservation.ReservationId, takeover.Term, "control-b", "gs-1", claim.Epoch,
            "successor-commit-1");
        Assert.Equal(RedesignMatchReservationStore.Committed, committed.Status);
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString,
            "SELECT count(*) FROM game.room WHERE tenant_id='tenant-a' AND game_id='game-a'"));
        Assert.Equal("Committed", (await tickets.GetTicketAsync("tenant-a", "game-a", b1.BindingId))!.State);
        Assert.Equal("Committed", (await tickets.GetTicketAsync("tenant-a", "game-a", b2.BindingId))!.State);
    }

    [Fact]
    public async Task AbortReleasesEverythingAndTheSweepAgrees()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var coordinator = new RedesignCoordinatorStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);
        var reservations = new RedesignMatchReservationStore(database.ConnectionString,
            maxActiveReservations: 1);

        var term = await coordinator.ClaimTermAsync(Partition, "control-a", 60);
        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-a1", "reserve-a1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-a2", "reserve-a2");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-1");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", b2.BindingId, b2.SessionId,
            Partition, "enqueue-op-2");
        var reservation = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-1",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            30, "reserve-op-1");
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-m-1", "gs-1", 30);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-m-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new RoundParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            "1.0.0", "cfg-1", "pkg-1", "room-op-1");

        // A stale term never aborts another coordinator's reservation.
        await Assert.ThrowsAsync<RedesignStaleTermException>(() => reservations.AbortReservationAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term + 1, "control-b",
            "abort-op-x"));

        var aborted = await reservations.AbortReservationAsync("tenant-a", "game-a",
            reservation.ReservationId, term.Term, "control-a", "abort-op-1");
        Assert.Equal(RedesignMatchReservationStore.Aborted, aborted.Status);
        var retry = await reservations.AbortReservationAsync("tenant-a", "game-a",
            reservation.ReservationId, term.Term, "control-a", "abort-op-1");
        Assert.True(retry.Duplicate);

        // The full release: room aborted, ownership released with an epoch bump, tickets back
        // in the queue with their reservation link cleared.
        Assert.Equal("Aborted", await ScalarAsync(database.ConnectionString,
            "SELECT status FROM game.room WHERE room_id='room-m-1'"));
        Assert.Equal("Released", await ScalarAsync(database.ConnectionString,
            "SELECT status FROM control.ownership WHERE kind='room' AND entity_id='room-m-1'"));
        Assert.Equal(claim.Epoch + 1, await ScalarAsync(database.ConnectionString,
            "SELECT epoch FROM control.ownership WHERE kind='room' AND entity_id='room-m-1'"));
        var ticket1 = await tickets.GetTicketAsync("tenant-a", "game-a", b1.BindingId);
        Assert.Equal(RedesignMatchTicketStore.Queued, ticket1!.State);
        Assert.Null(ticket1.ReservationId);
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString,
            "SELECT count(*) FROM infra.outbox WHERE effect_id='room-m-1/0/abort' AND kind='round.aborted'"));
        Assert.Equal(1L, await ScalarAsync(database.ConnectionString,
            $"SELECT count(*) FROM infra.outbox WHERE effect_id='{reservation.ReservationId}/abort' AND kind='match.aborted'"));

        // The sweep agrees there is nothing left; a later commit runs into the released
        // ownership fence, and the released slot reserves again.
        Assert.Equal(0, await reservations.ExpireReservationsAsync(10));
        await Assert.ThrowsAsync<RedesignStaleOwnerException>(() => reservations.CommitMatchAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term, "control-a",
            "gs-1", claim.Epoch, "commit-op-1"));
        await Assert.ThrowsAsync<RedesignMatchException>(() => reservations.AbortReservationAsync(
            "tenant-a", "game-a", reservation.ReservationId, term.Term, "control-a", "abort-op-2"));
        var reserved = await reservations.ReserveAsync("tenant-a", "game-a", Partition, term.Term,
            "control-a", "room-m-2",
            [new ReservedParticipant(b1.BindingId, p1.StreamerId, "Host"),
             new ReservedParticipant(b2.BindingId, p2.StreamerId, "Guest")],
            30, "reserve-op-2");
        Assert.Equal(RedesignMatchReservationStore.Reserved, reserved.Status);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ShiftReservationExpiryAsync(string connectionString, string roomId)
    {
        await ExecuteAsync(connectionString,
            "UPDATE match.reservation SET expires_at=clock_timestamp()-interval '1 second' WHERE room_id=@room",
            ("room", roomId));
    }

    private static async Task<(ResolvedStreamer Streamer, MountGrant Mount)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey,
        string openId)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", openId);
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "platform-room", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a",
            "op-1");
        return (streamer, new MountGrant(binding.BindingId, session.SessionId));
    }

    private sealed record MountGrant(string BindingId, string SessionId);

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m3_reservations_" + Guid.NewGuid().ToString("N");
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
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
