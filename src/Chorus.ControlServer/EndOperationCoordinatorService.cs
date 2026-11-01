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
/// pending and the re-push leg on every later pass carries it the same recorded command. Step
/// 4 (FIX-END-07) is the barrier the gateway acks drive: the phase records DeliveryStopped
/// when every responder in the frozen set confirmed at the recorded version — an empty set is
/// vacuously confirmed, one pending gateway parks the phase. Later phases belong to the later
/// items; a failure is recorded on the row without leaving the phase, so a restarted control
/// plane reads the phase and retries the same step.
/// </summary>
public sealed class EndOperationCoordinatorService(RedesignControlStore controlStore,
    StopDeliveryNotifier notifier, ILogger<EndOperationCoordinatorService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_END_COORDINATOR_INTERVAL_MS") ?? "1000",
        System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>How long a gateway's process reports may age before its route authorization
    /// counts as lapsed for the delivery-stop barrier (FIX-END-08) — the same
    /// fresh-process idiom the directory uses. A lapsed holder owes no confirmation: the
    /// receiver-side fence refuses its pushes for the closed round regardless, so the exit
    /// is safe exactly because the standing operation the barrier drives IS that fence.
    /// A holder that reports again re-enters the barrier and owes its answer anew.</summary>
    private static readonly int BarrierStaleSeconds = int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_END_BARRIER_STALE_SECONDS") ?? "60",
        System.Globalization.CultureInfo.InvariantCulture);

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
                // The re-push leg: every holder of route authorization hears the same recorded
                // command on every pass — a gateway offline at issue is carried, never
                // skipped, and a gateway that answered but restarted re-learns the closure
                // from the duplicate its fresh record accepts (FIX-END-07). FIX-END-08 makes
                // the barrier's truth the LIVE authorization set: a gateway that confirmed its
                // routing only after the issue joins the responders here (a missed instance
                // must not slip past), and a holder whose authorization lapsed — its process
                // stopped reporting — owes nothing, because the standing operation this phase
                // drives is itself the receiver-side fence that refuses its pushes.
                var command = await controlStore.GetStopDeliveryAsync(current.TenantId, current.GameId,
                    current.EndOperationId, cancellationToken);
                if (command is not null)
                {
                    var responders = await controlStore.ResolveDeliveryStopBarrierAsync(
                        current.TenantId, current.GameId, current.EndOperationId,
                        BarrierStaleSeconds, cancellationToken);
                    var holders = responders
                        .Where(responder => responder.HoldsAuthorization).ToList();
                    await PushAllAsync(command, current, holders, cancellationToken);
                    if (await controlStore.AdvanceDeliveryStoppedBarrierAsync(current.TenantId,
                        current.GameId, current.EndOperationId, BarrierStaleSeconds, cancellationToken))
                    {
                        var lapsed = responders.Count - holders.Count;
                        if (lapsed > 0)
                            logger.LogInformation(
                                "Delivery stopped for {RoomId} at version {Version}: every current holder " +
                                "confirmed; {Lapsed} responder's authorization lapsed pending its answer.",
                                command.RoomId, command.CloseRouteVersion, lapsed);
                        else
                            logger.LogInformation(
                                "Delivery stopped for {RoomId}: every responder confirmed at version {Version}.",
                                command.RoomId, command.CloseRouteVersion);
                    }
                }
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
