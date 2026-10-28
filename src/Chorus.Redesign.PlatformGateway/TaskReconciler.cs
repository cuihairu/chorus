using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.Redesign.PlatformGateway;

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
                // One task per concurrent branch: a call that hangs on the platform must not
                // starve the other due tasks (per-mount serialization is store-enforced, so a
                // colliding branch fails soft and returns next pass).
                await Task.WhenAll(ready.Tasks.Select(
                    task => ReconcileAsync(tasks, task, stoppingToken)));
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
            await LeaseAsync(tasks, task.TaskId, cancellationToken);
            await tasks.BeginAttemptAsync(new TaskAttemptRequest
            {
                TaskId = task.TaskId, OwnerInstanceId = instanceId, DeadlineSeconds = deadlineSeconds
            }, deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
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
        catch (Exception error) when (error is RpcException or OperationCanceledException
            or HttpRequestException)
        {
            logger?.LogWarning("Task {TaskId} pass stopped: {Message}", task.TaskId, error.Message);
        }
    }

    private async Task<TaskStateReply> LeaseAsync(PlatformTask.PlatformTaskClient tasks, string taskId,
        CancellationToken cancellationToken) => await tasks.AcquireLeaseAsync(new TaskLeaseRequest
    {
        TaskId = taskId, OwnerInstanceId = instanceId, LeaseSeconds = leaseSeconds
    }, deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
}
