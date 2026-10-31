using Chorus.Rpc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// The step-1 discovery net (architecture 6.4, FIX-END-04): a room whose result froze and
/// whose end operation never landed — a host killed after the final commit, a report whose
/// bounded retry exhausted, a control plane that was down at the moment — is begun here from
/// the room's own persisted receipt. The business id is the room's own, so this begin and the
/// host's report and a recovering host's replay all write the same one operation; whoever
/// arrives first creates it, the others answer Duplicate. The grace keeps the sweep behind a
/// live host's own report, so the fast path stays the fast path.
/// </summary>
public sealed class RoomEndDiscoveryService(RedesignControlStore controlStore,
    ILogger<RoomEndDiscoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_END_DISCOVERY_INTERVAL_MS") ?? "5000",
        System.Globalization.CultureInfo.InvariantCulture));
    private static readonly int GraceSeconds = int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_END_DISCOVERY_GRACE_SECONDS") ?? "10",
        System.Globalization.CultureInfo.InvariantCulture);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var room in await controlStore.ListUnreportedResultRoomsAsync(
                             GraceSeconds, stoppingToken))
                    await BeginOneAsync(room, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Room end discovery pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task BeginOneAsync(UnreportedFinishingRoom room, CancellationToken cancellationToken)
    {
        try
        {
            // The evidence is the room's own persisted receipt; the begin's own transaction
            // re-reads the mounts' current generations — a room that moved on between the
            // query and this begin refuses loudly and writes nothing.
            var (operation, _) = await controlStore.BeginEndOperationAsync(
                room.TenantId, room.GameId, room.RoomId, room.RoomId + "/end",
                room.LastTick, room.StateVersion, cancellationToken);
            logger.LogInformation(
                "Room {RoomId} ended without its operation; begun as {EndOperationId} at {Phase}.",
                room.RoomId, operation.EndOperationId, operation.Phase);
        }
        catch (StaleRoundGenerationException)
        {
            // The mounts released the round between the query and the begin — the host's own
            // report (or nothing, if the mounts ended without one) owns this room; the sweep
            // does not invent a begin for a round that is no longer the mounts'.
        }
        catch (EndOperationConflictException)
        {
            // One operation per room: a racing begin — the host's report landing right now —
            // won; the row exists and the ladder continues under it.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "Room end begin failed for {RoomId}", room.RoomId);
        }
    }
}
