using System.Text.Json;
using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>The coordinator's identity and cadence, resolved from its certificate and configuration.</summary>
public sealed record MatchCoordinatorOptions(string InstanceId, int IntervalMs, int DurationTicks = 40);

/// <summary>
/// The production match coordinator (architecture 7.1-7.3, 10.1): a ControlServer background
/// loop that holds a term per live partition, walks queued tickets into two-streamer matches,
/// and drives each reservation reserve → own → start → prepare → mark → commit. Every step
/// carries a deterministic operation id, so a successor instance that takes over the term
/// re-runs the same steps as visible duplicates: a prepared reservation commits without
/// building a second room, an orphan releases everything it held (AT10/AT11). Pairing stops
/// for the cycle on capacity, a lost term or an unreachable plane — the queue keeps the rest.
/// </summary>
public sealed class MatchCoordinatorService(
    ClusterDirectory directory,
    RedesignCoordinatorStore coordinatorStore,
    RedesignControlStore controlStore,
    RedesignAdminStore adminStore,
    MatchCoordinatorOptions options,
    ILogger<MatchCoordinatorService> logger) : BackgroundService
{
    private const int TermLeaseSeconds = 15;
    private const int ReservationSeconds = 15;
    private const int OwnershipLeaseSeconds = 30;
    private const int MaxPartitionsPerCycle = 16;
    private const int CandidateWindow = 16;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly MatchQueue.MatchQueueClient _queue = new(directory.DataChannel);
    private readonly Match.MatchClient _match = new(directory.DataChannel);
    private readonly RoundStore.RoundStoreClient _rounds = new(directory.DataChannel);
    private readonly Dictionary<string, TermStatus> _terms = [];

    /// <summary>A pairing candidate: the mount, the streamer, and the sides the example module deals them.</summary>
    private sealed record Candidate(string BindingId, string ParticipantId, string Role, string Faction);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The directory follows the process reports (FIX-SCHED-01); a coordinator without
        // live game hosts still recovers reservations but never pairs.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogWarning(error, "The match coordinator cycle failed.");
            }
            try
            {
                await Task.Delay(options.IntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task CycleAsync(CancellationToken cancellationToken)
    {
        // Discovery unions both halves: partitions with queued tickets pair, partitions with
        // unretired reservations recover — an orphan can be the only living thing in its
        // partition, with nothing queued behind it.
        var queued = (await _queue.ListPartitionsAsync(new Empty(),
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken)).PartitionKeys;
        var reserved = (await _match.ListReservationPartitionsAsync(new Empty(),
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken)).PartitionKeys;
        var partitions = queued.Concat(reserved).Distinct(StringComparer.Ordinal)
            .OrderBy(partition => partition, StringComparer.Ordinal).Take(MaxPartitionsPerCycle);
        // The admin queue gate parks pairing only: recovery of in-flight reservations continues,
        // so a suspended partition still converges its orphans — only new matches wait.
        var suspended = await adminStore.ListSuspendedPartitionsAsync(cancellationToken);
        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var term = await EnsureTermAsync(partition, cancellationToken);
            if (term is null) continue;
            await RecoverPartitionAsync(partition, term, cancellationToken);
            if (suspended.Contains(partition)) continue;
            await PairPartitionAsync(partition, term, cancellationToken);
        }
    }

    /// <summary>
    /// Keeps a term per partition: an unknown partition or one past half its lease is
    /// (re-)claimed. The claim is acquire-or-takeover — the same holder keeps its term, an
    /// expired or displaced lease starts the next one.
    /// </summary>
    private async Task<TermStatus?> EnsureTermAsync(string partition, CancellationToken cancellationToken)
    {
        if (_terms.TryGetValue(partition, out var held)
            && held.LeaseUntilUtc - DateTime.UtcNow > TimeSpan.FromSeconds(TermLeaseSeconds / 2d))
            return held;
        try
        {
            var claimed = await coordinatorStore.ClaimTermAsync(
                partition, options.InstanceId, TermLeaseSeconds, cancellationToken);
            _terms[partition] = claimed;
            return claimed;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "The term for partition {Partition} was not claimed.", partition);
            return null;
        }
    }

    private async Task RecoverPartitionAsync(string partition, TermStatus term,
        CancellationToken cancellationToken)
    {
        var active = await _match.ListActiveReservationsAsync(
            new ListActiveReservationsRequest { PartitionKey = partition, Max = 100 },
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken);
        foreach (var reservation in active.Reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RecoverReservationAsync(reservation, term, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Recovering reservation {ReservationId} failed.",
                    reservation.ReservationId);
            }
        }
    }

    /// <summary>
    /// A reservation of a previous cycle (this instance's or a predecessor's): prepared with a
    /// healthy room commits as-is; reserved with a healthy room re-drives the deterministic
    /// pipeline where every retry is a duplicate; anything else is an orphan whose tickets
    /// must go back to the queue (AT11).
    /// </summary>
    private async Task RecoverReservationAsync(ReservationProto reservation, TermStatus term,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(CallTimeout);
        var ownership = await controlStore.GetOwnershipAsync(
            reservation.TenantId, reservation.GameId, "room", reservation.RoomId, cancellationToken);
        var healthy = ownership is { Status: "Active" }
            && ownership.LeaseUntilUtc > DateTime.UtcNow;
        if (reservation.Status == "Prepared" && healthy)
        {
            await _match.CommitMatchAsync(new CommitMatchRequest
            {
                TenantId = reservation.TenantId, GameId = reservation.GameId,
                ReservationId = reservation.ReservationId, CoordinatorTerm = term.Term,
                HolderInstanceId = options.InstanceId, RoomInstanceId = ownership!.OwnerInstanceId,
                RoomEpoch = ownership.Epoch, OperationId = "commit-" + reservation.RoomId
            }, deadline: deadline, cancellationToken: cancellationToken);
            return;
        }
        var gameChannel = ownership is null ? null : directory.GameHostChannel(ownership.OwnerInstanceId);
        if (reservation.Status == "Reserved" && healthy && gameChannel is not null)
        {
            var candidates = reservation.Participants.Select(participant => new Candidate(
                participant.BindingId, participant.ParticipantId, participant.Role,
                participant.Role == "Host" ? "red" : "blue")).ToArray();
            await DriveRoomAsync(reservation.TenantId, reservation.GameId, reservation.ReservationId,
                term, reservation.RoomId, candidates, ownership!,
                new GameHost.GameHostClient(gameChannel), cancellationToken);
            return;
        }
        await _match.AbortReservationAsync(new AbortReservationRequest
        {
            TenantId = reservation.TenantId, GameId = reservation.GameId,
            ReservationId = reservation.ReservationId, CoordinatorTerm = term.Term,
            HolderInstanceId = options.InstanceId, OperationId = "abort-" + reservation.ReservationId
        }, deadline: deadline, cancellationToken: cancellationToken);
    }

    private async Task PairPartitionAsync(string partition, TermStatus term,
        CancellationToken cancellationToken)
    {
        if (directory.GameHostCount == 0) return;
        var window = (await _queue.ListQueuedAsync(
            new ListQueuedRequest { PartitionKey = partition, Max = CandidateWindow },
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken)).Tickets;
        for (var i = 0; i + 1 < window.Count; i += 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var host = window[i];
            var guest = window[i + 1];
            if (host.TenantId != guest.TenantId || host.GameId != guest.GameId)
            {
                logger.LogWarning("Partition {Partition} mixes tenants or games; not pairing.", partition);
                return;
            }
            var roomId = "room-" + Guid.NewGuid().ToString("N");
            var candidates = new[]
            {
                new Candidate(host.BindingId, host.StreamerId, "Host", "red"),
                new Candidate(guest.BindingId, guest.StreamerId, "Guest", "blue")
            };
            try
            {
                await PairAsync(partition, term, host.TenantId, host.GameId, roomId, candidates,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (RpcException error) when (error.StatusCode is StatusCode.ResourceExhausted
                or StatusCode.FailedPrecondition or StatusCode.Unavailable)
            {
                logger.LogWarning("Pairing stopped for partition {Partition}: {Status} — {Message}.",
                    partition, error.StatusCode, error.Message);
                return;
            }
        }
    }

    private async Task PairAsync(string partition, TermStatus term, string tenantId, string gameId,
        string roomId, Candidate[] candidates, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(CallTimeout);
        var reserved = await _match.ReserveMatchAsync(new ReserveMatchRequest
        {
            TenantId = tenantId, GameId = gameId, PartitionKey = partition,
            CoordinatorTerm = term.Term, HolderInstanceId = options.InstanceId, RoomId = roomId,
            ReservationSeconds = ReservationSeconds, OperationId = "reserve-" + roomId,
            Participants = { candidates.Select(candidate => new ReservedParticipantProto
            {
                BindingId = candidate.BindingId, ParticipantId = candidate.ParticipantId,
                Role = candidate.Role
            }) }
        }, deadline: deadline, cancellationToken: cancellationToken);
        var reservationId = reserved.Reservation.ReservationId;
        var (gameInstanceId, gameChannel) = directory.NextGameHost();
        var ownership = await controlStore.ClaimOwnershipAsync(
            tenantId, gameId, "room", roomId, gameInstanceId, OwnershipLeaseSeconds, cancellationToken);
        try
        {
            await DriveRoomAsync(tenantId, gameId, reservationId, term, roomId, candidates, ownership,
                new GameHost.GameHostClient(gameChannel), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            // Half-enrolled matches never persist: release the reservation so the tickets
            // requeue now instead of waiting for the deadline sweep.
            logger.LogWarning(error, "Match {RoomId} failed mid-pipeline; releasing the reservation.", roomId);
            try
            {
                await _match.AbortReservationAsync(new AbortReservationRequest
                {
                    TenantId = tenantId, GameId = gameId, ReservationId = reservationId,
                    CoordinatorTerm = term.Term, HolderInstanceId = options.InstanceId,
                    OperationId = "abort-" + reservationId
                }, deadline: deadline, cancellationToken: cancellationToken);
            }
            catch (Exception abortError) when (abortError is not OperationCanceledException)
            {
                logger.LogWarning(abortError,
                    "Releasing reservation {ReservationId} failed; the deadline sweep owns it now.",
                    reservationId);
            }
            throw;
        }
    }

    /// <summary>
    /// The shared tail of pairing and recovery: start (a duplicate when the predecessor already
    /// did), prepare tick-0, mark the receipt, commit under the ownership fence. Same operation
    /// ids on every drive, so the second run is all duplicates.
    /// </summary>
    private async Task DriveRoomAsync(string tenantId, string gameId, string reservationId,
        TermStatus term, string roomId, IReadOnlyList<Candidate> candidates, OwnerStatus ownership,
        GameHost.GameHostClient gameHost, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(CallTimeout);
        await _rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = ownership.OwnerInstanceId, Epoch = ownership.Epoch,
            GameId = gameId, ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-" + roomId, TenantId = tenantId,
            Participants = { candidates.Select(candidate => new RoundParticipantProto
            {
                BindingId = candidate.BindingId, ParticipantId = candidate.ParticipantId,
                Role = candidate.Role
            }) }
        }, deadline: deadline, cancellationToken: cancellationToken);
        var prepared = await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = roomId, GameId = gameId, OwnerInstanceId = ownership.OwnerInstanceId,
            Epoch = ownership.Epoch, OperationId = "prepare-" + roomId,
            DurationTicks = options.DurationTicks, RandomSeed = SeedFor(roomId), TickMs = 25,
            Participants = { candidates.Select(candidate => new GameParticipantProto
            {
                BindingId = candidate.BindingId, ParticipantId = candidate.ParticipantId,
                Faction = candidate.Faction
            }) }
        }, deadline: deadline, cancellationToken: cancellationToken);
        await _match.MarkPreparedAsync(new MarkPreparedRequest
        {
            TenantId = tenantId, GameId = gameId, ReservationId = reservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = options.InstanceId,
            PreparedReceiptJson = JsonSerializer.Serialize(new
            {
                prepared.Status, prepared.StateVersion, prepared.Tick
            }),
            OperationId = "mark-" + roomId
        }, deadline: deadline, cancellationToken: cancellationToken);
        await _match.CommitMatchAsync(new CommitMatchRequest
        {
            TenantId = tenantId, GameId = gameId, ReservationId = reservationId,
            CoordinatorTerm = term.Term, HolderInstanceId = options.InstanceId,
            RoomInstanceId = ownership.OwnerInstanceId, RoomEpoch = ownership.Epoch,
            OperationId = "commit-" + roomId
        }, deadline: deadline, cancellationToken: cancellationToken);
    }

    /// <summary>A stable seed per room: recovery re-prepares the same initial state bit for bit.</summary>
    private static long SeedFor(string roomId)
        => BitConverter.ToInt64(Convert.FromHexString(roomId.Substring("room-".Length, 16)));
}
