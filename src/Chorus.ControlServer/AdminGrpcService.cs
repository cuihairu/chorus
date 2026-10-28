using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// The admin boundary (operations 6): diagnostics that join the whole footprint of a streamer
/// or a room, the AT31 round trace (event → delivery → frame → effects → result → grants →
/// ledger), and controlled failure commands. Every command names a request id, an operator and
/// a reason; the audit row is written first, so a command replays as a visible duplicate
/// instead of acting twice — a failed command keeps its request id spent, and the operator
/// retries under a new one. Reads never mutate.
/// </summary>
public sealed class AdminGrpcService(
    ClusterDirectory directory, RedesignAdminStore adminStore,
    TrustedProcessCertificates trustedCertificates) : Admin.AdminBase
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly RoundStore.RoundStoreClient _rounds = new(directory.DataChannel);
    private readonly Settlement.SettlementClient _settlement = new(directory.DataChannel);
    private readonly PlatformTask.PlatformTaskClient _tasks = new(directory.DataChannel);

    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<AdminStreamerSnapshotReply> GetStreamerSnapshot(
        AdminStreamerSnapshotRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.StreamerId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The snapshot request is incomplete."));
        var snapshot = await adminStore.GetStreamerSnapshotAsync(
            request.TenantId, request.StreamerId, context.CancellationToken);
        var reply = new AdminStreamerSnapshotReply
        {
            Found = snapshot.Found, StreamerId = snapshot.StreamerId, BanState = snapshot.BanState,
            CurrentSessionId = snapshot.CurrentSessionId ?? "", SessionVersion = snapshot.SessionVersion,
            TicketPresent = snapshot.Ticket is not null, OwnershipPresent = snapshot.Ownership is not null,
            OwnerInstanceId = snapshot.Ownership?.OwnerInstanceId ?? "",
            OwnershipEpoch = snapshot.Ownership?.Epoch ?? 0,
            OwnershipStatus = snapshot.Ownership?.Status ?? ""
        };
        foreach (var binding in snapshot.Bindings)
            reply.Bindings.Add(new AdminBindingProto
            {
                BindingId = binding.BindingId, GameId = binding.GameId, Generation = binding.Generation,
                State = binding.State, CurrentRoomId = binding.CurrentRoomId ?? "",
                CreatedUnixMs = new DateTimeOffset(binding.CreatedAtUtc).ToUnixTimeMilliseconds()
            });
        if (snapshot.Ticket is { } ticket)
            reply.Ticket = new TicketProto
            {
                TicketId = ticket.TicketId, State = ticket.State, PartitionKey = ticket.PartitionKey,
                ReservationId = ticket.ReservationId ?? "", Version = ticket.Version,
                StreamerId = snapshot.StreamerId, TenantId = request.TenantId
            };
        foreach (var task in snapshot.Tasks)
            reply.Tasks.Add(new AdminTaskProto
            {
                TaskId = task.TaskId, OperationId = task.OperationId, Status = task.Status,
                AttemptCount = task.AttemptCount, TaskEpoch = task.TaskEpoch
            });
        return reply;
    }

    public override async Task<AdminRoomSnapshotReply> GetRoomSnapshot(
        AdminRoomSnapshotRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        if (string.IsNullOrWhiteSpace(request.RoomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The snapshot request is incomplete."));
        var snapshot = await adminStore.GetRoomSnapshotAsync(request.RoomId, context.CancellationToken);
        return new AdminRoomSnapshotReply
        {
            Found = snapshot.Found, TenantId = snapshot.TenantId, GameId = snapshot.GameId,
            Status = snapshot.Status, Epoch = snapshot.Epoch, LastTick = snapshot.LastTick,
            StateVersion = snapshot.StateVersion, OwnershipPresent = snapshot.Ownership is not null,
            OwnerInstanceId = snapshot.Ownership?.OwnerInstanceId ?? "",
            OwnershipEpoch = snapshot.Ownership?.Epoch ?? 0,
            OwnershipStatus = snapshot.Ownership?.Status ?? "",
            Participants = { snapshot.Participants.Select(participant => new AdminParticipantProto
            {
                ParticipantId = participant.ParticipantId, BindingId = participant.BindingId ?? "",
                Role = participant.Role, State = participant.State
            }) },
            ResultVersion = snapshot.ResultVersion, FinalFrame = snapshot.FinalFrame,
            GrantsTotal = snapshot.GrantsTotal, GrantsGranted = snapshot.GrantsGranted
        };
    }

    public override async Task<AdminTraceRoundReply> TraceRound(
        AdminTraceRoundRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        if (string.IsNullOrWhiteSpace(request.RoomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The trace request is incomplete."));
        var trace = await adminStore.TraceRoundAsync(request.RoomId, context.CancellationToken);
        return new AdminTraceRoundReply
        {
            Found = trace.Found, Status = trace.Status, Epoch = trace.Epoch, LastTick = trace.LastTick,
            Events = { trace.Events.Select(item => new AdminTraceEventProto
            {
                Sequence = item.Sequence, EventId = item.EventId, Disposition = item.Disposition,
                BindingId = item.BindingId ?? "",
                ReceivedUnixMs = new DateTimeOffset(item.ReceivedAtUtc).ToUnixTimeMilliseconds(),
                PayloadJson = item.PayloadJson
            }) },
            Frames = { trace.Frames.Select(item => new AdminTraceFrameProto
            {
                Tick = item.Tick, CommitId = item.CommitId, InputRefsJson = item.InputRefsJson,
                EffectsJson = item.EffectsJson
            }) },
            Effects = { trace.Effects.Select(item => new AdminTraceEffectProto
            {
                EffectId = item.EffectId, Kind = item.Kind, Status = item.Status, PayloadJson = item.PayloadJson
            }) },
            ResultVersion = trace.ResultVersion, ResultJson = trace.ResultJson,
            Grants = { trace.Grants.Select(item => new AdminTraceGrantProto
            {
                PlayerId = item.PlayerId, GrantKey = item.GrantKey, Status = item.Status,
                OperationId = item.OperationId
            }) },
            Ledger = { trace.Ledger.Select(item => new AdminTraceLedgerProto
            {
                OperationId = item.OperationId, LineId = item.LineId, PlayerId = item.PlayerId,
                AssetId = item.AssetId, Delta = item.Delta, Reason = item.Reason,
                ResultingBalance = item.ResultingBalance,
                CommittedUnixMs = new DateTimeOffset(item.CommittedAtUtc).ToUnixTimeMilliseconds()
            }) }
        };
    }

    public override async Task<AdminQueueGateReply> SuspendMatchQueue(
        AdminQueueGateRequest request, ServerCallContext context) =>
        await QueueGateAsync(request, context, suspended: true, "SuspendMatchQueue");

    public override async Task<AdminQueueGateReply> ResumeMatchQueue(
        AdminQueueGateRequest request, ServerCallContext context) =>
        await QueueGateAsync(request, context, suspended: false, "ResumeMatchQueue");

    private async Task<AdminQueueGateReply> QueueGateAsync(
        AdminQueueGateRequest request, ServerCallContext context, bool suspended, string command)
    {
        Require(context, "control-server");
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.PartitionKey))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The command needs a request id, an operator, a reason and a partition."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.PartitionKey, command, $$"""{"partition":"{{request.PartitionKey}}","suspended":{{(suspended ? "true" : "false")}}}""",
            context.CancellationToken);
        if (!acted)
        {
            // A replayed command reports the gate as it stands now — a later resume must not be
            // undone by a duplicate of an earlier suspend.
            var current = await adminStore.GetQueueGateAsync(request.PartitionKey, context.CancellationToken);
            return new AdminQueueGateReply
            {
                PartitionKey = request.PartitionKey,
                Suspended = current?.Suspended ?? !suspended, Duplicate = true
            };
        }
        var gate = await adminStore.SetQueueGateAsync(
            request.PartitionKey, suspended, request.Operator, context.CancellationToken);
        return new AdminQueueGateReply
        {
            PartitionKey = gate.PartitionKey, Suspended = gate.Suspended, Duplicate = false
        };
    }

    public override async Task<AdminRoomRecoveryReply> RetryRoomRecovery(
        AdminRoomRecoveryRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.ToInstanceId) ||
            request.TickMs <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The recovery request is incomplete."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.RoomId, "RetryRoomRecovery",
            $$"""{"room":"{{request.RoomId}}","to":"{{request.ToInstanceId}}"}""", context.CancellationToken);
        if (!acted)
            return new AdminRoomRecoveryReply { Duplicate = true };
        // The same completion TakeoverRoom drives: confirm the new holder's ownership, then the
        // named instance replays the round from the head at the confirmed epoch.
        var channel = directory.GameHostChannel(request.ToInstanceId)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"The recovery target '{request.ToInstanceId}' is not a known game host."));
        var confirm = await _rounds.ConfirmRoomOwnershipAsync(new ConfirmRoomOwnershipRequest
        {
            TenantId = request.TenantId, GameId = request.GameId, RoomId = request.RoomId,
            OwnerInstance = request.ToInstanceId
        }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        var recovered = await new GameHost.GameHostClient(channel).RecoverRoomAsync(
            new RecoverRoomRequest
            {
                RoomId = request.RoomId, GameId = request.GameId, OwnerInstanceId = request.ToInstanceId,
                Epoch = confirm.Epoch, TickMs = request.TickMs
            }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        return new AdminRoomRecoveryReply
        {
            OwnerInstanceId = request.ToInstanceId, Epoch = confirm.Epoch, Recovered = recovered.Recovered,
            Status = recovered.Status, LastTick = recovered.LastTick
        };
    }

    public override async Task<AdminDrainProcessReply> DrainProcess(
        AdminDrainProcessRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.ProcessInstanceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The drain request is incomplete."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.ProcessInstanceId, "DrainProcess",
            $$"""{"process":"{{request.ProcessInstanceId}}"}""", context.CancellationToken);
        if (!acted)
        {
            var currentLifecycle = await adminStore.ReadProcessLifecycleAsync(
                request.ProcessInstanceId, context.CancellationToken);
            return new AdminDrainProcessReply
            {
                ProcessInstanceId = request.ProcessInstanceId, Found = currentLifecycle is not null,
                Lifecycle = currentLifecycle ?? "", Duplicate = true
            };
        }
        var lifecycle = await adminStore.DrainProcessAsync(request.ProcessInstanceId, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"The process '{request.ProcessInstanceId}' is not registered."));
        return new AdminDrainProcessReply
        {
            ProcessInstanceId = request.ProcessInstanceId, Found = true, Lifecycle = lifecycle
        };
    }

    public override async Task<AdminSettlementRetryReply> RetrySettlement(
        AdminSettlementRetryRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.RoomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The retry request is incomplete."));
        var room = await adminStore.GetRoomSnapshotAsync(request.RoomId, context.CancellationToken);
        if (!room.Found)
            throw new RpcException(new Status(StatusCode.NotFound, "The room does not exist."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.RoomId, "RetrySettlement",
            $$"""{"room":"{{request.RoomId}}","tenant":"{{room.TenantId}}","game":"{{room.GameId}}"}""",
            context.CancellationToken);
        if (!acted)
            return new AdminSettlementRetryReply
            {
                RoomId = request.RoomId, ResultVersion = room.ResultVersion, GrantsTotal = room.GrantsTotal,
                GrantsGranted = room.GrantsGranted, GrantsPending = room.GrantsTotal - room.GrantsGranted,
                Duplicate = true
            };
        // CommitResult is itself idempotent per grant receipt: already-granted lines replay as
        // zero pending work, and only the incomplete grants are retried.
        var retry = await _settlement.CommitResultAsync(new CommitResultRequest
        {
            RoomId = request.RoomId, GameId = room.GameId, TenantId = room.TenantId,
            OperationId = "admin:" + request.RequestId
        }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        return new AdminSettlementRetryReply
        {
            RoomId = retry.RoomId, ResultVersion = retry.ResultVersion, GrantsTotal = retry.GrantsTotal,
            GrantsGranted = retry.GrantsGranted, GrantsPending = retry.GrantsPending
        };
    }

    public override async Task<AdminTaskReconcileReply> ReconcilePlatformTask(
        AdminTaskReconcileRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.TaskId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The reconcile request is incomplete."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.TaskId, "ReconcilePlatformTask",
            $$"""{"task":"{{request.TaskId}}"}""", context.CancellationToken);
        if (!acted)
            return new AdminTaskReconcileReply { TaskId = request.TaskId, Duplicate = true };
        // Only a NeedsAttention task requeues — the audited decision to try again, then the
        // executors pick it up through the normal lease path.
        var task = await _tasks.RequeueTaskAsync(new TaskRequeueRequest { TaskId = request.TaskId },
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        return new AdminTaskReconcileReply
        {
            TaskId = task.TaskId, Status = task.Status, TaskEpoch = task.TaskEpoch
        };
    }

    private static bool Incomplete(string requestId, string operation, string reason) =>
        string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(operation) ||
        string.IsNullOrWhiteSpace(reason);

    private ProcessIdentity Require(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
        return caller;
    }
}
