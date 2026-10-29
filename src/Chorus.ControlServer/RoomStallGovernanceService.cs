using Chorus.Redesign.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// The stall governor (FIX-TAKE-07, design §4): a host whose heartbeat stays healthy can
/// still hold a room whose actor stopped. The process lease and business liveness stay two
/// different signals — the heartbeat never masks a stalled room, and a stalled room never
/// by itself questions the host's other rooms. The classified policy: a deterministic
/// module fault is a pause, not a migration (the actor already parked the room Suspended
/// and the same fault would repeat on any host); a silently frozen head — or a stop that
/// is not a module fault — migrates the room to a different host under a newer epoch once
/// the stall has persisted past the threshold. The epoch match is the latch: the frozen
/// evidence only fires while the Active ownership still names exactly that generation, so
/// a transferred or recovering room never re-fires here and brief slow frames — heads that
/// keep moving — never reach this service at all. The migration budget bounds churn: a
/// room that keeps needing moves waits for an operator instead of bouncing forever. Old
/// writes stay fenced by the data plane: the transfer bumps the epoch and the frozen
/// host's next commit is refused, never guessed around.
/// </summary>
public sealed class RoomStallGovernanceService(RedesignControlStore controlStore, ClusterDirectory directory,
    ILogger<RoomStallGovernanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_STALL_SWEEP_INTERVAL_MS") ?? "5000",
        System.Globalization.CultureInfo.InvariantCulture));
    private static readonly int StallSeconds = int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_STALL_SECONDS") ?? "30",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly int FreshSeconds = int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_FRESH_SECONDS") ?? "20",
        System.Globalization.CultureInfo.InvariantCulture);

    // The lease a stall migration grants; long enough for a recovery to land, short enough
    // that the deadline sweep (FIX-TAKE-05) reclaims a target that never finishes.
    private const int MigrationLeaseSeconds = 15;

    // A room that keeps needing moves stops being moved automatically: after this many
    // recorded operations it waits for an operator instead of churning epochs forever.
    private const int MigrationBudget = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var stalled in await controlStore.ListStalledRoomsAsync(
                    StallSeconds, FreshSeconds, stoppingToken))
                    await GovernOneAsync(stalled, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Room stall governance pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task GovernOneAsync(RedesignControlStore.StalledRoom stalled, CancellationToken cancellationToken)
    {
        if (stalled.StopReason is { } reason && reason.Contains("module failed", StringComparison.Ordinal))
            return; // parked by the actor already; the fault is deterministic, migration repeats it
        if (stalled.TenantId is null || stalled.GameId is null || stalled.TickMs <= 0)
            return; // the room's own row is gone or unreadable: nothing to move, nothing to guess
        var ownership = await controlStore.GetOwnershipAsync(
            stalled.TenantId, stalled.GameId, "room", stalled.RoomId, cancellationToken);
        // The latch: the frozen evidence only acts while the Active ownership still names
        // exactly the generation it froze in — anything else means someone is already
        // acting (Recovering) or the evidence is stale against a newer generation.
        if (ownership is not { Status: "Active" } || ownership.OwnerInstanceId != stalled.ProcessInstanceId
            || ownership.Epoch != stalled.Epoch)
            return;
        if (await controlStore.GetOpenRoomRecoveryAsync(
            stalled.TenantId, stalled.GameId, stalled.RoomId, cancellationToken) is not null)
            return; // a recovery is already in flight
        if (await controlStore.CountRoomRecoveryOperationsAsync(
            stalled.TenantId, stalled.GameId, stalled.RoomId, cancellationToken) >= MigrationBudget)
        {
            logger.LogWarning(
                "Room {RoomId} is stalled again but its migration budget is spent; it waits for an operator",
                stalled.RoomId);
            return;
        }
        var target = directory.GameHostIds.FirstOrDefault(host => host != stalled.ProcessInstanceId);
        if (target is null)
        {
            logger.LogWarning("Room {RoomId} is stalled but no replacement host is configured", stalled.RoomId);
            return;
        }
        var operationId = $"stall/{stalled.RoomId}/{stalled.Epoch}";
        try
        {
            // The same sequence as the operator takeover (FIX-TAKE-03/04): the transfer
            // fences the frozen host from that instant, the target recovers under the
            // Recovering fence, and the data plane's Active answer — not the recoverer's
            // word — decides the outcome. The deadline sweep is the safety net when this
            // process dies between the two steps.
            var rounds = new RoundStore.RoundStoreClient(directory.DataChannel);
            var moved = await rounds.TransferRoomOwnershipAsync(new TransferRoomOwnershipRequest
            {
                TenantId = stalled.TenantId, GameId = stalled.GameId, RoomId = stalled.RoomId,
                FromInstance = ownership.OwnerInstanceId, ToInstance = target,
                LeaseSeconds = MigrationLeaseSeconds, OperationId = operationId
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            var channel = directory.GameHostChannel(target)
                ?? throw new InvalidOperationException($"The stall target '{target}' left the directory.");
            var recovered = await new GameHost.GameHostClient(channel).RecoverRoomAsync(
                new RecoverRoomRequest
                {
                    RoomId = stalled.RoomId, GameId = stalled.GameId, OwnerInstanceId = target,
                    Epoch = moved.Epoch, TickMs = stalled.TickMs
                }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            var after = await controlStore.GetOwnershipAsync(
                stalled.TenantId, stalled.GameId, "room", stalled.RoomId, cancellationToken);
            if (recovered.Recovered && after is not { Status: "Active" })
                throw new InvalidOperationException(
                    $"The recovery reported success but the ownership is {after?.Status}.");
            await controlStore.CompleteRoomRecoveryAsync(stalled.TenantId, stalled.GameId, stalled.RoomId,
                recovered.Recovered, recovered.LastTick, recovered.Recovered ? null : recovered.Note,
                cancellationToken);
            logger.LogWarning(
                "Room {RoomId} stalled on {Instance}; recovered on {Target} at epoch {Epoch} to tick {Tick}",
                stalled.RoomId, stalled.ProcessInstanceId, target, moved.Epoch, recovered.LastTick);
        }
        catch (Exception error) when (error is RpcException or InvalidOperationException)
        {
            // A refused step leaves the decision to the next pass or to the deadline sweep;
            // nothing here extends a deadline or re-enables an old epoch.
            logger.LogWarning(error, "Room {RoomId} stall migration {OperationId} was refused",
                stalled.RoomId, operationId);
        }
    }
}
