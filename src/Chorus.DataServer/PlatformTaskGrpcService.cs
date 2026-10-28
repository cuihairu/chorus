using Chorus.Data.Redesign;
using Chorus.Redesign.Rpc;
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
                request.DesiredJson, context.CancellationToken);
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
            Require(context, "control-server");
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
        TaskEpoch = state.TaskEpoch, OperationId = state.OperationId, Status = state.Status,
        AttemptCount = state.AttemptCount, DesiredJson = state.DesiredJson,
        ObservedJson = state.ObservedJson ?? "", LastError = state.LastError ?? "",
        NextAttemptUnixMs = state.NextAttemptUtc?.ToUnixTimeMilliseconds() ?? 0,
        LeaseOwner = state.LeaseOwner ?? "",
        LeaseUntilUnixMs = state.LeaseUntilUtc?.ToUnixTimeMilliseconds() ?? 0,
        AttemptOwner = state.AttemptOwner ?? "",
        AttemptDeadlineUnixMs = state.AttemptDeadlineUtc?.ToUnixTimeMilliseconds() ?? 0,
        Duplicate = state.Duplicate
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
