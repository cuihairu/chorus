using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// Closes the drain loop (FIX-AGENT-02, architecture 4.24): a game slot whose desired state
/// is Draining keeps its process alive while it finishes the rooms it already holds — the
/// agent no longer kills on Draining. This sweep watches the draining game slots, counts
/// the Active room leases each host still owns, and escalates the slot to Drained (fresh
/// command id) once the count reaches zero, or the slot has no live process row at all.
/// Only game-server slots escalate automatically: room ownership is the one business state
/// the control database can see. Other roles wait for an operator's Drained plan or the
/// agent's force deadline (CHORUS_DRAIN_FORCE_SECONDS).
/// </summary>
public sealed class DrainConfirmationService(
    RedesignControlStore store, ILogger<DrainConfirmationService> logger) : BackgroundService
{
    private readonly int _intervalMs =
        int.TryParse(Environment.GetEnvironmentVariable("CHORUS_DRAIN_CONFIRM_INTERVAL_MS"), out var parsed)
        && parsed > 0 ? parsed : 2000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var plan in await store.FindDrainingGameSlotsAsync(stoppingToken))
                {
                    var processInstanceId = await store.FindSlotProcessInstanceAsync(plan.SlotId, stoppingToken);
                    var rooms = processInstanceId is null
                        ? 0
                        : await store.CountActiveRoomOwnershipsAsync(processInstanceId, stoppingToken);
                    if (rooms > 0)
                    {
                        logger.LogInformation(
                            "Drain confirmation: slot {SlotId} still holds {Rooms} active rooms; waiting",
                            plan.SlotId, rooms);
                        continue;
                    }
                    var commandId = $"drain-confirmed-{plan.SlotId}-{plan.PlanGeneration}";
                    if (await store.EscalateDrainedAsync(plan.SlotId, plan.PlanGeneration, commandId,
                            stoppingToken))
                        logger.LogInformation(
                            "Drain confirmed: slot {SlotId} holds no active rooms; escalated to Drained (command {CommandId})",
                            plan.SlotId, commandId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                // The sweep is best-effort: a store hiccup delays the escalation until the
                // next tick, and the agent's force deadline still bounds the wait end to end.
                logger.LogWarning(error, "Drain confirmation sweep failed; retrying next tick");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(_intervalMs), stoppingToken);
        }
    }
}
