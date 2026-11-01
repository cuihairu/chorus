using Chorus.Rpc;
using Grpc.Core;
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
/// vacuously confirmed, one pending gateway parks the phase. Step 6 (FIX-END-10) closes the
/// platform side: the phase records PlatformClosed when every mount's end task answered
/// success or a grounded NotRequired. Step 7 (FIX-END-12) is the release: the coordinator —
/// not the original game host — asks the data plane to finalize, and the data plane's own
/// transaction re-checks the whole evidence (the ladder's platform-side phases, the inputs'
/// terminal dispositions, the manifest's completion) before it atomically frees the round's
/// pointers, participations, held capacity and ownership. Step 8 (FIX-END-14) destroys the
/// settled round's runtime entities on the game hosts — a broadcast whose epoch ceiling is
/// the released ownership row's own bump, so the destroy is structurally post-release and a
/// newer-generation actor is never touched — and Done closes the ladder. A failure is
/// recorded on the row without leaving the phase, so a restarted control plane reads the
/// phase and retries the same step.
/// </summary>
public sealed class EndOperationCoordinatorService(RedesignControlStore controlStore,
    StopDeliveryNotifier notifier, ClusterDirectory directory,
    ILogger<EndOperationCoordinatorService> logger)
    : BackgroundService
{
    private readonly PlatformTask.PlatformTaskClient _tasks = new(directory.DataChannel);
    private readonly Settlement.SettlementClient _settlement = new(directory.DataChannel);

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
                // FIX-END-09: the platform end tasks are recorded now — durably bound to
                // this operation's barrier credential — but their executors may not call
                // until the ladder reaches PlatformTaskAuthorized below. Recording early is
                // the point: the task waits on the barrier, the barrier never waits on the
                // recording, and a restart re-enqueues the same (binding, operation) row
                // idempotently. A data plane the channel cannot reach holds the recording,
                // never the delivery-stop leg: the StopDeliveryIssued pass retries it.
                await TryEnqueuePlatformEndTasksAsync(command, current, cancellationToken);
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
                    // The recording retry (FIX-END-09): a task the ClosingInput step could
                    // not reach the data plane to write lands here, idempotently, while the
                    // barrier gathers its answers. Authorization below is safe either way —
                    // a task recorded after the barrier completed can only ever call after
                    // it completed.
                    await TryEnqueuePlatformEndTasksAsync(command, current, cancellationToken);
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
                        // Step 5 (FIX-END-09): the barrier stood complete, so the platform
                        // end tasks' credential is minted — the phase row itself. Executors
                        // read it live before any call leaves their process.
                        current = await controlStore.AdvanceEndOperationPhaseAsync(current.TenantId,
                            current.GameId, current.EndOperationId, EndOperationPhases.DeliveryStopped,
                            EndOperationPhases.PlatformTaskAuthorized, cancellationToken: cancellationToken);
                        logger.LogInformation(
                            "Platform end tasks authorized for {RoomId}: {Bindings} binding(s) may close on the platform.",
                            command.RoomId, current.BindingGenerations.Count);
                    }
                }
            }
            if (current.Phase == EndOperationPhases.PlatformTaskAuthorized)
            {
                // Step 6 (FIX-END-10): the platform close's credential is the tasks' own
                // standing — every mount's end task answered success or a grounded
                // NotRequired. A count short of the operation's recorded mounts is the
                // recording still in flight (the enqueue retries above), never a vacuous
                // completion; an unknown, failed or attention task keeps the operation open —
                // a timeout may not advance as success. A data plane the channel cannot
                // reach holds the read, never the ladder: the next pass asks again.
                try
                {
                    var close = await _tasks.GetCloseCompletionAsync(new TaskCloseRequest
                    {
                        EndOperationId = current.EndOperationId,
                        ExpectedTasks = current.BindingGenerations
                            .DistinctBy(binding => binding.BindingId).Count()
                    }, deadline: DateTime.UtcNow.AddSeconds(5),
                        cancellationToken: cancellationToken);
                    if (close.Complete)
                    {
                        await controlStore.AdvanceEndOperationPhaseAsync(current.TenantId,
                            current.GameId, current.EndOperationId,
                            EndOperationPhases.PlatformTaskAuthorized, EndOperationPhases.PlatformClosed,
                            cancellationToken: cancellationToken);
                        logger.LogInformation(
                            "Platform closed for {RoomId}: {Completed} executed, {NotRequired} not required.",
                            current.RoomId, close.Completed, close.NotRequired);
                    }
                }
                catch (RpcException error)
                {
                    logger.LogWarning(error,
                        "Platform close completion for {RoomId} waits for the data plane: {Message}",
                        current.RoomId, error.Message);
                }
            }
            if (current.Phase == EndOperationPhases.PlatformClosed)
            {
                // Step 7 (FIX-END-12): the release is the control plane's own call — the
                // terminator does not depend on the original game host. The business id is
                // the end operation itself, so a coordinator restart replays into the same
                // receipt: whichever pass fired first owns the release, every later pass
                // reads it back as a duplicate. The data plane's transaction re-checks the
                // whole evidence — the ladder's phases recorded here, the inputs' terminal
                // dispositions and the manifest's completion recorded there — and refuses
                // visibly when any of it is still gathering; a refusal parks this phase and
                // the next pass asks again, so a settle still in flight (FIX-END-11's
                // executor or the host's own settle) merely delays the release.
                try
                {
                    var released = await _settlement.FinalizeRoundAsync(new FinalizeRoundRequest
                    {
                        TenantId = current.TenantId, GameId = current.GameId,
                        RoomId = current.RoomId, OperationId = current.EndOperationId
                    }, deadline: DateTime.UtcNow.AddSeconds(30),
                        cancellationToken: cancellationToken);
                    current = await controlStore.AdvanceEndOperationPhaseAsync(current.TenantId,
                        current.GameId, current.EndOperationId,
                        EndOperationPhases.PlatformClosed, EndOperationPhases.Released,
                        cancellationToken: cancellationToken);
                    logger.LogInformation(
                        "Released {RoomId}: the round is settled at epoch {Epoch}{Duplicate}.",
                        current.RoomId, released.Epoch,
                        released.Duplicate ? " (a duplicate receipt — the release already stood)" : "");
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
                {
                    // The evidence is still gathering — most often the manifest mid-settle.
                    // The refusal names what the data plane's transaction found missing; the
                    // phase parks and the next pass asks again.
                    logger.LogInformation(
                        "The release of {RoomId} waits for its evidence: {Message}",
                        current.RoomId, error.Message);
                }
                catch (RpcException error)
                {
                    logger.LogWarning(error,
                        "The release of {RoomId} waits for the data plane: {Message}",
                        current.RoomId, error.Message);
                }
            }
            if (current.Phase == EndOperationPhases.Released)
            {
                // Step 8 (FIX-END-14): the settled round's runtime entities leave the game
                // hosts. The ceiling is the ownership row's released epoch — the finalize
                // transaction's own bump — so the destroy is structurally post-release: a
                // pre-release row's epoch equals the running epoch and every host spares
                // everything at or above it, while an actor a takeover raised after the
                // release is a newer generation no destroy may touch. The broadcast reaches
                // every live host because the entities die with a dead host's process: the
                // directory's liveness set is exactly the set that can hold one. The destroy
                // never depends on the original actor — an unknown or already-destroyed room
                // answers clean — so a host that cannot be reached is the only thing worth
                // waiting for: the phase parks and the next pass asks again, and a host that
                // truly stopped reporting leaves the directory and with it the wait.
                var owner = await controlStore.GetOwnershipAsync(current.TenantId, current.GameId,
                    "room", current.RoomId, cancellationToken);
                if (owner is null)
                {
                    logger.LogWarning(
                        "The destroy of {RoomId} waits: the released ownership row is not readable yet.",
                        current.RoomId);
                }
                else
                {
                    var destroyedHosts = 0;
                    var sparedHosts = 0;
                    foreach (var instanceId in directory.GameHostIds)
                    {
                        var channel = directory.GameHostChannel(instanceId);
                        if (channel is null) continue; // left between the snapshot and the resolve
                        var reply = await new GameHost.GameHostClient(channel).DestroyRoomAsync(
                            new DestroyRoomRequest
                            {
                                TenantId = current.TenantId, GameId = current.GameId,
                                RoomId = current.RoomId, ReleaseEpoch = owner.Epoch,
                                EndOperationId = current.EndOperationId
                            }, deadline: DateTime.UtcNow.AddSeconds(5),
                            cancellationToken: cancellationToken);
                        if (reply.Destroyed) destroyedHosts++;
                        if (reply.SparedNewGeneration) sparedHosts++;
                    }
                    current = await controlStore.AdvanceEndOperationPhaseAsync(current.TenantId,
                        current.GameId, current.EndOperationId,
                        EndOperationPhases.Released, EndOperationPhases.Destroyed,
                        cancellationToken: cancellationToken);
                    // Done closes the ladder: the row leaves the open list, and the standing
                    // input fence retires with it — the settled status column and the
                    // generation fences carry the late-event refusals from here (END-06).
                    current = await controlStore.AdvanceEndOperationPhaseAsync(current.TenantId,
                        current.GameId, current.EndOperationId,
                        EndOperationPhases.Destroyed, EndOperationPhases.Done,
                        cancellationToken: cancellationToken);
                    logger.LogInformation(
                        "Destroyed {RoomId} below epoch {Epoch}: {Destroyed} host(s) removed entities, " +
                        "{Spared} host(s) spared a newer generation.",
                        current.RoomId, owner.Epoch, destroyedHosts, sparedHosts);
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

    /// <summary>Records one platform end task per mount binding, bound to the standing
    /// operation's barrier (FIX-END-09). The operation id — the platform's idempotency key —
    /// is the end operation itself: one platform close per ended round, replayed safely
    /// across coordinator restarts by the (binding, operation) row identity. The desired
    /// state carries the operation's category declaration (FIX-END-10): the round end's
    /// scope is the round — the mount and the live outlive it and close in their own later
    /// operations — so a plain round change can never unbind the gameplay or end the live,
    /// and the grounds of those two non-calls are recorded on the task, not implied by
    /// silence.</summary>
    private async Task EnqueuePlatformEndTasksAsync(StopDeliveryCommand command,
        EndOperation operation, CancellationToken cancellationToken)
    {
        foreach (var binding in operation.BindingGenerations)
        {
            var desired = System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "end-round",
                command.TenantId, command.GameId, command.RoomId, command.EndOperationId,
                command.RoundGeneration, command.CloseRouteVersion,
                bindingId = binding.BindingId, bindingGeneration = binding.Generation,
                bindingRoundGeneration = binding.RoundGeneration,
                categories = new Dictionary<string, string>
                {
                    ["roundEnd"] = "apply",
                    ["gameplayUnbind"] = "not-required:the round end's scope is the round",
                    ["liveEnd"] = "not-required:the round end's scope is the round"
                }
            });
            await _tasks.EnqueueDesiredAsync(new TaskEnqueueRequest
            {
                BindingId = binding.BindingId,
                OperationId = command.EndOperationId,
                DesiredJson = desired,
                BarrierEndOperationId = command.EndOperationId
            }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
        }
    }

    /// <summary>The enqueue's transport faults are the data plane's outage, not this
    /// operation's: logged and retried on the next pass, so a dead data channel holds the
    /// platform task's recording without ever holding the delivery-stop leg hostage.</summary>
    private async Task TryEnqueuePlatformEndTasksAsync(StopDeliveryCommand command,
        EndOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            await EnqueuePlatformEndTasksAsync(command, operation, cancellationToken);
        }
        catch (Exception error) when (error is RpcException or HttpRequestException)
        {
            logger.LogWarning(error,
                "Platform end task recording for {RoomId} waits for the data plane: {Message}",
                command.RoomId, error.Message);
        }
    }

    private async Task PushAllAsync(StopDeliveryCommand command, EndOperation operation,
        IReadOnlyList<StopDeliveryResponder> responders, CancellationToken cancellationToken)
    {
        foreach (var responder in responders)
            await notifier.PushAsync(command, operation, responder, cancellationToken);
    }
}
