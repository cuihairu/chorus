using Chorus.Data;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// External platform task boundary (architecture 6.3). Desired state and lease reassignment
/// belong to the control plane; attempts belong to the gateway that holds the lease. Every
/// mutating call is one store transaction — a reply always reflects a committed task state.
/// </summary>
public sealed class PlatformTaskGrpcService(
    RedesignPlatformTaskStore taskStore, TrustedProcessCertificates trustedCertificates)
    : PlatformTask.PlatformTaskBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "platform-gateway", "streamer-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override Task<TaskStateReply> EnqueueDesired(TaskEnqueueRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "control-server", "streamer-server");
            return taskStore.EnqueueDesiredAsync(request.BindingId, request.OperationId,
                request.DesiredJson,
                request.BarrierEndOperationId.Length == 0 ? null : request.BarrierEndOperationId,
                context.CancellationToken);
        });

    public override Task<TaskStateReply> AcquireLease(TaskLeaseRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "control-server", "platform-gateway");
            return taskStore.AcquireLeaseAsync(request.TaskId, request.OwnerInstanceId,
                TimeSpan.FromSeconds(request.LeaseSeconds), context.CancellationToken);
        });

    public override Task<TaskStateReply> BeginAttempt(TaskAttemptRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "platform-gateway");
            return taskStore.BeginAttemptAsync(request.TaskId, request.OwnerInstanceId,
                TimeSpan.FromSeconds(request.DeadlineSeconds), context.CancellationToken);
        });

    public override Task<TaskStateReply> CompleteAttempt(TaskCompleteRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "platform-gateway");
            return taskStore.CompleteAttemptAsync(request.TaskId, request.OwnerInstanceId,
                request.ObservedJson, context.CancellationToken);
        });

    /// <summary>FIX-END-10: the executor's terminal answer for a platform that requires no
    /// call for this operation — the observed json is the capability ground, and the task
    /// closes exactly like a completed one.</summary>
    public override Task<TaskStateReply> CompleteNotRequired(TaskCompleteRequest request,
        ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "platform-gateway");
            return taskStore.CompleteNotRequiredAsync(request.TaskId, request.OwnerInstanceId,
                request.ObservedJson, context.CancellationToken);
        });

    public override Task<TaskStateReply> MarkOutcomeUnknown(TaskErrorRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "platform-gateway");
            return taskStore.MarkOutcomeUnknownAsync(request.TaskId, request.OwnerInstanceId,
                request.LastError, context.CancellationToken);
        });

    public override Task<TaskStateReply> MarkRetryFailed(TaskRetryRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "platform-gateway");
            return taskStore.MarkRetryFailedAsync(request.TaskId, request.OwnerInstanceId,
                request.LastError, DateTimeOffset.FromUnixTimeMilliseconds(request.NextAttemptUnixMs),
                context.CancellationToken);
        });

    public override Task<TaskStateReply> MarkNeedsAttention(TaskStopRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            // FIX-END-10 widens the caller to the executor: the gateway holding the lease is
            // the one that knows the outcome is unknown AND the platform cannot answer for
            // it AND its own re-issue is not idempotent — the three facts that leave human
            // attention as the only safe move. The control plane keeps the same call.
            Require(context, "control-server", "platform-gateway");
            return taskStore.MarkNeedsAttentionAsync(request.TaskId, request.LastError,
                context.CancellationToken);
        });

    public override Task<TaskStateReply> ResolveOutcome(TaskResolveRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "control-server", "platform-gateway");
            return taskStore.ResolveOutcomeAsync(request.TaskId, request.OwnerInstanceId,
                request.ExecutedByPlatform, request.ObservedJson.Length == 0 ? null : request.ObservedJson,
                context.CancellationToken);
        });

    public override async Task<TaskStateReply> GetTask(TaskGetRequest request, ServerCallContext context)
    {
        Require(context, "control-server", "platform-gateway", "streamer-server");
        var state = await taskStore.GetTaskAsync(request.TaskId, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "The platform task does not exist."));
        return Reply(state);
    }

    public override async Task<TaskListReply> ListReadyTasks(TaskListRequest request, ServerCallContext context)
    {
        Require(context, "control-server", "platform-gateway");
        var tasks = await taskStore.ListReadyTasksAsync(
            request.Max is <= 0 or > 64 ? 16 : request.Max, context.CancellationToken);
        return new TaskListReply { Tasks = { tasks.Select(Reply) } };
    }

    /// <summary>FIX-END-09: the executor's pre-call barrier check. Executors ask before they
    /// take any lease at all, so an unauthorized end task never enters the lease protocol
    /// through this face — the store's BeginAttempt refusal is the same predicate read
    /// authoritatively for an executor that skipped its own question.</summary>
    public override async Task<TaskBarrierReply> GetBarrierAuthorization(
        TaskBarrierRequest request, ServerCallContext context)
    {
        Require(context, "control-server", "platform-gateway");
        var barrier = await taskStore.GetBarrierAuthorizationAsync(request.TaskId,
            context.CancellationToken);
        return new TaskBarrierReply { Authorized = barrier.Authorized, Phase = barrier.Phase };
    }

    public override Task<TaskStateReply> RequeueTask(TaskRequeueRequest request, ServerCallContext context) =>
        ExecuteAsync(() =>
        {
            Require(context, "control-server");
            return taskStore.RequeueTaskAsync(request.TaskId, context.CancellationToken);
        });

    /// <summary>FIX-END-10: the coordinator's read of one end operation's platform-close
    /// standing — the credential the ladder's PlatformClosed step is built on.</summary>
    public override async Task<TaskCloseReply> GetCloseCompletion(TaskCloseRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server");
        if (request.ExpectedTasks < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The expected task count cannot be negative."));
        var close = await taskStore.GetCloseCompletionAsync(request.EndOperationId,
            request.ExpectedTasks, context.CancellationToken);
        return new TaskCloseReply
        {
            Complete = close.Complete, Tasks = close.Tasks, Completed = close.Completed,
            NotRequired = close.NotRequired, Outstanding = close.Outstanding
        };
    }

    private static async Task<TaskStateReply> ExecuteAsync(Func<Task<PlatformTaskState>> action)
    {
        try
        {
            return Reply(await action());
        }
        catch (TaskBusyException)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "Another external operation is executing on this mount."));
        }
        catch (TaskStateConflictException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The platform task does not accept this transition now."));
        }
        catch (TaskBarrierException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The platform end task is not authorized: the delivery-stop barrier has not completed."));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (BindingDeniedException)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The gameplay binding is not available."));
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    private static TaskStateReply Reply(PlatformTaskState state) => new()
    {
        TaskId = state.TaskId, BindingId = state.BindingId, BindingGeneration = state.BindingGeneration,
        Platform = state.Platform, AppId = state.AppId,
        TaskEpoch = state.TaskEpoch, OperationId = state.OperationId, Status = state.Status,
        AttemptCount = state.AttemptCount, DesiredJson = state.DesiredJson,
        ObservedJson = state.ObservedJson ?? "", LastError = state.LastError ?? "",
        NextAttemptUnixMs = state.NextAttemptUtc?.ToUnixTimeMilliseconds() ?? 0,
        LeaseOwner = state.LeaseOwner ?? "",
        LeaseUntilUnixMs = state.LeaseUntilUtc?.ToUnixTimeMilliseconds() ?? 0,
        AttemptOwner = state.AttemptOwner ?? "",
        AttemptDeadlineUnixMs = state.AttemptDeadlineUtc?.ToUnixTimeMilliseconds() ?? 0,
        Duplicate = state.Duplicate,
        BarrierEndOperationId = state.BarrierEndOperationId ?? ""
    };

    private void Require(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
    }
}
