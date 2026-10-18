using Chorus.Control;
using Chorus.Engine.Routing;

namespace Chorus.ControlHost;

public sealed class ControlMaintenance(RoomControlStore store, ILogger<ControlMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var iteration = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var room in store.Rooms().Where(r => r.State == RoomLifecycleState.Migrating))
                {
                    try { store.Handoff(room.Id, room.Epoch); }
                    catch (ControlConflictException ex) { logger.LogWarning("Handoff pending for {RoomId}: {Reason}",room.Id,ex.Message); }
                }
                store.FormMatches();
                if (++iteration % 5 == 0) store.Sample();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Control maintenance failed; authority remains fenced"); }
        }
    }
}
