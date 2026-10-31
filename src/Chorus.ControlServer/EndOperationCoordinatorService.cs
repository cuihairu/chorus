using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// The end sequence's driver (architecture 6.4, FIX-END-05): walks each open operation's
/// recorded phase forward. Step 2 (ClosingInput) is the marker — the receiver-side
/// input-generation fence reads this standing row (FIX-END-06): every admission leg refuses
/// new authoritative input while the operation stands, independent of the room's status
/// column, without touching ownership; step 3 persists the
/// stop-delivery command with its responder set and pushes it at every authorized gateway,
/// then records StopDeliveryIssued: the persisted command and the pending set are the step's
/// evidence, so the phase advances even when a push could not land — an offline gateway stays
/// pending and the re-push leg on every later pass carries it the same recorded command. Later
/// phases belong to the later items; a failure is recorded on the row without leaving the
/// phase, so a restarted control plane reads the phase and retries the same step.
/// </summary>
public sealed class EndOperationCoordinatorService(RedesignControlStore controlStore,
    StopDeliveryNotifier notifier, ILogger<EndOperationCoordinatorService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_END_COORDINATOR_INTERVAL_MS") ?? "1000",
        System.Globalization.CultureInfo.InvariantCulture));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var operation in await controlStore.ListOpenEndOperationsAsync(stoppingToken))
                    await DriveAsync(operation, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "End operation coordination pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DriveAsync(EndOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            var current = operation;
            // Step 2 stands recorded; the standing row itself is the receiver-side input fence
            // (FIX-END-06) — the data plane refuses new authoritative input while it stands.
            if (current.Phase == EndOperationPhases.ResultFrozen)
                current = await controlStore.AdvanceEndOperationPhaseAsync(current.TenantId,
                    current.GameId, current.EndOperationId, EndOperationPhases.ResultFrozen,
                    EndOperationPhases.ClosingInput, cancellationToken: cancellationToken);
            if (current.Phase == EndOperationPhases.ClosingInput)
            {
                var (command, _, responders) = await controlStore.IssueStopDeliveryAsync(
                    current.TenantId, current.GameId, current.EndOperationId, cancellationToken);
                await PushAllAsync(command, current, responders, cancellationToken);
                current = await controlStore.AdvanceEndOperationPhaseAsync(current.TenantId,
                    current.GameId, current.EndOperationId, EndOperationPhases.ClosingInput,
                    EndOperationPhases.StopDeliveryIssued, cancellationToken: cancellationToken);
                logger.LogInformation(
                    "Stop delivery issued for {RoomId} at version {Version} to {Responders} gateways.",
                    command.RoomId, command.CloseRouteVersion, responders.Count);
            }
            if (current.Phase == EndOperationPhases.StopDeliveryIssued)
            {
                // The re-push leg: responders still pending hear the same recorded command on
                // every pass — a gateway offline at issue is carried, never skipped.
                var command = await controlStore.GetStopDeliveryAsync(current.TenantId, current.GameId,
                    current.EndOperationId, cancellationToken);
                if (command is not null)
                    await PushAllAsync(command, current,
                        (await controlStore.ListStopDeliveryRespondersAsync(current.TenantId,
                            current.GameId, current.EndOperationId, cancellationToken))
                        .Where(responder => responder.State == "Pending").ToList(),
                        cancellationToken);
            }
        }
        catch (EndOperationConflictException)
        {
            // Another pass — or a restart's racing driver — moved the row first; the next sweep
            // reads the phase exactly as it stands.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "End operation step failed for {RoomId}", operation.RoomId);
            try
            {
                await controlStore.RecordEndOperationErrorAsync(operation.TenantId, operation.GameId,
                    operation.EndOperationId, error.Message, cancellationToken);
            }
            catch (Exception recordError)
            {
                logger.LogWarning(recordError, "Recording the end operation failure itself failed");
            }
        }
    }

    private async Task PushAllAsync(StopDeliveryCommand command, EndOperation operation,
        IReadOnlyList<StopDeliveryResponder> responders, CancellationToken cancellationToken)
    {
        foreach (var responder in responders)
            await notifier.PushAsync(command, operation, responder, cancellationToken);
    }
}
