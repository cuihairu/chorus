using Chorus.Redesign.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// The recovery-deadline sweep (FIX-TAKE-05, architecture failure table): an open room
/// recovery whose recorded deadline passed is never extended — the sweep closes it and
/// either moves the room to a different configured host under a newer epoch, or, with no
/// replacement (or the reassignment budget spent), parks the operation Failed for an
/// operator. A target that is alive but never finishes appears here exactly like a dead
/// one: the deadline, not liveness, decides. The displaced target cannot revive its epoch —
/// the confirm path only admits the holder the ownership names.
/// </summary>
public sealed class RoomRecoverySweepService(RedesignControlStore controlStore, ClusterDirectory directory,
    ILogger<RoomRecoverySweepService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_RECOVERY_SWEEP_INTERVAL_MS") ?? "5000",
        System.Globalization.CultureInfo.InvariantCulture));

    // The lease the sweep grants a reassigned recovery; the room's own transfer leases stay
    // caller-chosen.
    private const int ReassignLeaseSeconds = 15;

    // A room that keeps timing out stops being moved automatically: after this many recorded
    // operations it parks for an operator instead of churning epochs forever.
    private const int ReassignmentBudget = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var expired in await controlStore.ListExpiredOpenRoomRecoveriesAsync(stoppingToken))
                    await SweepOneAsync(expired, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Room recovery sweep pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task SweepOneAsync(RoomRecoveryOperation expired, CancellationToken cancellationToken)
    {
        // FIX-TAKE-08: a recovery whose reply was lost still converges by the persistent
        // state. The ownership row is the authority, so an expired operation whose room
        // already runs Active under exactly its target and epoch is simply recorded
        // Activated — never reassigned away from a healthy holder, never re-bumped.
        var ownership = await controlStore.GetOwnershipAsync(
            expired.TenantId, expired.GameId, "room", expired.RoomId, cancellationToken);
        if (ownership is { Status: "Active" } && ownership.OwnerInstanceId == expired.ToInstance
            && ownership.Epoch == expired.TargetEpoch)
        {
            await controlStore.CompleteRoomRecoveryAsync(expired.TenantId, expired.GameId, expired.RoomId,
                recovered: true, lastTick: expired.LastTick, lastError: null, cancellationToken);
            logger.LogInformation(
                "Room {RoomId} recovery {OperationId} already converged on {Instance}; recorded Activated",
                expired.RoomId, expired.OperationId, expired.ToInstance);
            return;
        }
        var attempts = await controlStore.CountRoomRecoveryOperationsAsync(
            expired.TenantId, expired.GameId, expired.RoomId, cancellationToken);
        var replacement = directory.GameHostIds
            .FirstOrDefault(host => host != expired.ToInstance);
        if (attempts > ReassignmentBudget || replacement is null)
        {
            // Parked, not extended: the room stays fenced behind its Recovering ownership and
            // the recorded failure names the cause for the operator.
            var reason = replacement is null
                ? "The recovery deadline passed with no replacement host; the operation waits for an operator."
                : "The recovery deadline passed and the reassignment budget is spent; the operation waits for an operator.";
            await controlStore.CompleteRoomRecoveryAsync(expired.TenantId, expired.GameId, expired.RoomId,
                recovered: false, lastTick: 0, lastError: reason, cancellationToken);
            logger.LogWarning("Room {RoomId} recovery {OperationId} parked: {Reason}",
                expired.RoomId, expired.OperationId, reason);
            return;
        }
        try
        {
            var rounds = new RoundStore.RoundStoreClient(directory.DataChannel);
            var moved = await rounds.ReassignRoomRecoveryAsync(new ReassignRoomRecoveryRequest
            {
                TenantId = expired.TenantId, GameId = expired.GameId, RoomId = expired.RoomId,
                OperationId = expired.OperationId,
                NewOperationId = $"reassign/{expired.RoomId}/{expired.OperationId}",
                FromInstance = expired.ToInstance, ToInstance = replacement,
                LeaseSeconds = ReassignLeaseSeconds
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            logger.LogWarning(
                "Room {RoomId} recovery {OperationId} timed out; reassigned to {Instance} at epoch {Epoch}",
                expired.RoomId, expired.OperationId, moved.OwnerInstanceId, moved.Epoch);
        }
        catch (RpcException error)
        {
            // A completed recovery or a lost race refuses the move — the next pass re-reads
            // the operation and decides again; nothing here extends a deadline.
            logger.LogWarning(error, "Room {RoomId} recovery reassignment of {OperationId} was refused",
                expired.RoomId, expired.OperationId);
        }
    }
}
