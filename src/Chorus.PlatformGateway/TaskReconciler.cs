using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.PlatformGateway;

/// <summary>
/// The platform-task executor loop (architecture 6.3): each pass takes the lease protocol's due
/// work — Pending and Retryable tasks, OutcomeUnknown verifications, and tasks a dead executor
/// left Executing with an expired lease. A fresh attempt only follows the outbox rule (the call
/// is durably recorded before it is issued) and a taken-over task is never re-called blindly:
/// the platform's official record decides executed-versus-retryable first. The loop is
/// fail-soft: one task's error never stops the pass, and the next pass retries.
/// </summary>
public sealed class TaskReconciler(
    Func<GrpcChannel> channelFactory, IPlatformTaskAdapter adapter, string instanceId,
    int intervalMs, int leaseSeconds, int deadlineSeconds, int callTimeoutSeconds,
    ILogger<TaskReconciler>? logger = null) : BackgroundService
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long one branch rides out transient lease/attempt RPC faults before giving
    /// the task back to the loop (which retries once the lease it may hold expires). Under
    /// full-suite load a data server can stall for tens of seconds; the window must ride that
    /// out or the task parks invisible until its lease expires.</summary>
    private static readonly TimeSpan BeginRetryWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan BeginRetryBackoff = TimeSpan.FromSeconds(1);

    /// <summary>Task ids with a branch in flight. Detached branches mean the loop never waits
    /// on a hung platform call, and the set keeps one task from running in two branches at
    /// once (a listed task is skipped until its branch finishes).</summary>
    private readonly object _inFlightGate = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var channel = channelFactory();
        var tasks = new PlatformTask.PlatformTaskClient(channel);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ready = await tasks.ListReadyTasksAsync(new TaskListRequest { Max = 16 },
                    deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: stoppingToken);
                foreach (var task in ready.Tasks)
                {
                    lock (_inFlightGate)
                    {
                        if (!_inFlight.Add(task.TaskId)) continue;
                    }
                    _ = ReconcileAsync(tasks, task, stoppingToken);
                }
            }
            catch (Exception error) when (error is RpcException or OperationCanceledException
                or HttpRequestException)
            {
                logger?.LogWarning("Task reconciliation pass failed: {Message}", error.Message);
            }
            try { await Task.Delay(intervalMs, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task ReconcileAsync(PlatformTask.PlatformTaskClient tasks, TaskStateReply task,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileCoreAsync(tasks, task, cancellationToken);
        }
        // Detached from the loop, so every fault lands here: expected transport faults log and
        // fail soft, anything else is logged at error instead of surfacing as an unobserved
        // task exception — one task's failure never stops the pass or the service.
        catch (Exception error) when (error is RpcException or OperationCanceledException
            or HttpRequestException)
        {
            logger?.LogWarning("Task {TaskId} pass stopped: {Message}", task.TaskId, error.Message);
        }
        catch (Exception error)
        {
            logger?.LogError(error, "Task {TaskId} branch failed unexpectedly", task.TaskId);
        }
        finally
        {
            lock (_inFlightGate) _inFlight.Remove(task.TaskId);
        }
    }

    private async Task ReconcileCoreAsync(PlatformTask.PlatformTaskClient tasks,
        TaskStateReply task, CancellationToken cancellationToken)
    {
        // A task another executor left behind, or one parked unknown: the epoch cannot
        // cancel a call already sent, so the old attempt's deadline bounds the wait before
        // verification. Take the lease, ask the platform for the official outcome, and let
        // the record decide — executed completes without a second call, not-executed
        // becomes retryable immediately.
        if (task.Status is "Executing" or "OutcomeUnknown")
        {
            if (task.AttemptDeadlineUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                return;
            await LeaseAsync(tasks, task.TaskId, cancellationToken);
            var outcome = await adapter.QueryAsync(task.OperationId, cancellationToken);
            await tasks.ResolveOutcomeAsync(new TaskResolveRequest
            {
                TaskId = task.TaskId, OwnerInstanceId = instanceId,
                ExecutedByPlatform = outcome.Executed,
                ObservedJson = outcome.ObservedJson ?? ""
            }, deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
            return;
        }
        // Due work: lease, record the attempt (its outbox effect is part of the same store
        // transaction), then issue the call and land the observed state. A call that could
        // not be observed parks OutcomeUnknown — this loop or a successor resolves it by
        // query; it is never re-issued blindly.
        if (!await BeginDueAttemptAsync(tasks, task, cancellationToken)) return;
        using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callCancellation.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, callTimeoutSeconds)));
        try
        {
            var observed = await adapter.CallAsync(
                new PlatformTaskCall(task.OperationId, task.DesiredJson), callCancellation.Token);
            if (observed.Executed)
            {
                await tasks.CompleteAttemptAsync(new TaskCompleteRequest
                {
                    TaskId = task.TaskId, OwnerInstanceId = instanceId,
                    ObservedJson = observed.ObservedJson ?? """{"executed":true}"""
                }, deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
            }
            else
            {
                await tasks.MarkRetryFailedAsync(new TaskRetryRequest
                {
                    TaskId = task.TaskId, OwnerInstanceId = instanceId,
                    LastError = "The platform did not execute the operation.",
                    NextAttemptUnixMs = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds()
                }, deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            await tasks.MarkOutcomeUnknownAsync(new TaskErrorRequest
            {
                TaskId = task.TaskId, OwnerInstanceId = instanceId,
                LastError = "The platform call could not be observed: " + error.Message
            }, deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Takes the lease and records the attempt for due work, riding out transient transport
    /// faults: a lost reply may have committed either step, so the stored state — not the
    /// exception — decides whether to retry. A committed lease is never re-acquired (that
    /// would bump the epoch without an attempt), and a committed attempt is never re-begun;
    /// both are detected by reading the task back. A conflicting owner or a state that no
    /// longer accepts the transition gives up for this pass — the loop retries when the task
    /// is due again.
    /// </summary>
    private async Task<bool> BeginDueAttemptAsync(PlatformTask.PlatformTaskClient tasks,
        TaskStateReply task, CancellationToken cancellationToken)
    {
        var giveUpAt = DateTime.UtcNow.Add(BeginRetryWindow);
        var leased = false;
        while (true)
        {
            try
            {
                if (!leased)
                {
                    await LeaseAsync(tasks, task.TaskId, cancellationToken);
                    leased = true;
                }
                await tasks.BeginAttemptAsync(new TaskAttemptRequest
                {
                    TaskId = task.TaskId, OwnerInstanceId = instanceId, DeadlineSeconds = deadlineSeconds
                }, deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
                return true;
            }
            catch (RpcException error)
                when (DateTime.UtcNow < giveUpAt && error.StatusCode is StatusCode.DeadlineExceeded
                    or StatusCode.Unavailable or StatusCode.Internal or StatusCode.Unknown)
            {
                // The store, not the exception, says what committed.
                var state = await tasks.GetTaskAsync(new TaskGetRequest { TaskId = task.TaskId },
                    deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
                if (state is null) return false;
                if (!leased && state.LeaseOwner == instanceId &&
                    state.LeaseUntilUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    leased = true;
                if (state.AttemptOwner == instanceId && state.Status is "Executing" or "Completed")
                    return state.Status == "Executing";
                if (state.LeaseOwner != instanceId || state.Status is not ("Pending" or "Retryable"))
                    return false;
                await Task.Delay(BeginRetryBackoff, cancellationToken);
            }
            catch (RpcException)
            {
                // A definitive refusal (conflicting owner, busy mount, stale state): the task
                // belongs to someone else's protocol step now.
                return false;
            }
        }
    }

    private async Task<TaskStateReply> LeaseAsync(PlatformTask.PlatformTaskClient tasks, string taskId,
        CancellationToken cancellationToken) => await tasks.AcquireLeaseAsync(new TaskLeaseRequest
    {
        TaskId = taskId, OwnerInstanceId = instanceId, LeaseSeconds = leaseSeconds
    }, deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
}
