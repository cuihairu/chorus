using Chorus.Data;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// Round admission and routing boundary (architecture 6.2, 6.3). Admission and abort belong to
/// the control plane and the room owner; routing belongs to the gateway feeding rounds; client
/// commands arrive through the streamer path. The tenant is the caller's certificate tenant;
/// every mutating call is one store transaction, so a reply always reflects a committed state.
/// </summary>
public sealed class RoundGrpcService(
    RedesignRoomStore roomStore, TrustedProcessCertificates trustedCertificates)
    : RoundStore.RoundStoreBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "game-server", "streamer-server", "platform-gateway");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<RoundAdmissionReply> StartRound(StartRoundRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server");
        try
        {
            var admission = await roomStore.StartRoundAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.RoomId,
                request.OwnerInstanceId, request.Epoch,
                request.Participants.Select(participant => new RoundParticipant(
                    participant.BindingId, participant.ParticipantId, participant.Role)).ToList(),
                request.ModuleVersion, request.ConfigHash, request.PackageHash, request.OperationId,
                context.CancellationToken);
            return Reply(admission);
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The room ownership fence rejected the caller."));
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (RedesignCapacityException error)
        {
            // Pressure refuses the round and is counted (AT21); it never deletes to make room.
            throw new RpcException(new Status(StatusCode.ResourceExhausted, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<RoundAdmissionReply> AbortRound(AbortRoundRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server");
        try
        {
            var admission = await roomStore.AbortRoundAsync(caller.TenantId, request.GameId, request.RoomId,
                request.OwnerInstanceId, request.Epoch, request.Reason, request.OperationId,
                context.CancellationToken);
            return Reply(admission);
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The room ownership fence rejected the caller."));
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<RoundAdmissionReply> PrepareRound(PrepareRoundRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server");
        try
        {
            var admission = await roomStore.PrepareRoundAsync(caller.TenantId, request.GameId, request.RoomId,
                request.OwnerInstanceId, request.Epoch, request.SnapshotPayload.ToByteArray(),
                request.SnapshotHash.ToByteArray(), request.SnapshotSchemaVersion, request.OperationId,
                request.TickMs > 0 ? request.TickMs : 50,
                request.RecoveryMode.Length > 0 ? request.RecoveryMode : "DeterministicReplay",
                context.CancellationToken);
            return Reply(admission);
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The room ownership fence rejected the caller."));
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<RoundAdmissionReply> ActivateRound(ActivateRoundRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server");
        try
        {
            var admission = await roomStore.ActivateRoundAsync(caller.TenantId, request.GameId, request.RoomId,
                request.OwnerInstanceId, request.Epoch, request.OperationId, context.CancellationToken);
            return Reply(admission);
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The room ownership fence rejected the caller."));
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<RoundAdmissionReply> SuspendRound(SuspendRoundRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server");
        try
        {
            var admission = await roomStore.SuspendRoundAsync(caller.TenantId, request.GameId, request.RoomId,
                request.OwnerInstanceId, request.Epoch, request.Reason, request.OperationId,
                context.CancellationToken);
            return Reply(admission);
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The room ownership fence rejected the caller."));
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<RoomTakeoverReply> TransferRoomOwnership(
        TransferRoomOwnershipRequest request, ServerCallContext context)
    {
        var caller = Require(context, "control-server");
        try
        {
            var takeover = await roomStore.TransferRoomOwnershipAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.RoomId,
                request.FromInstance, request.ToInstance, request.LeaseSeconds, request.OperationId,
                context.CancellationToken);
            return TakeoverReply(takeover);
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "The room has no ownership under this scope."));
        }
        catch (InvalidOperationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The operation was retried with a different transfer."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<RoomTakeoverReply> ReassignRoomRecovery(
        ReassignRoomRecoveryRequest request, ServerCallContext context)
    {
        // Control-plane only (FIX-TAKE-05): the recovery sweep's move of a timed-out open
        // operation — never a caller-facing takeover path.
        var caller = Require(context, "control-server");
        try
        {
            var takeover = await roomStore.ReassignRoomRecoveryAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.RoomId,
                request.OperationId, request.NewOperationId, request.FromInstance, request.ToInstance,
                request.LeaseSeconds, context.CancellationToken);
            return TakeoverReply(takeover);
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "The room has no ownership under this scope."));
        }
        catch (InvalidOperationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<RoomTakeoverReply> ConfirmRoomOwnership(
        ConfirmRoomOwnershipRequest request, ServerCallContext context)
    {
        // The new holder confirms once its recovery pinned the verified head (FIX-TAKE-03);
        // the control plane completes takeovers with the same call.
        var caller = Require(context, "control-server", "game-server");
        try
        {
            var takeover = await roomStore.ConfirmRoomOwnershipAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.RoomId,
                request.OwnerInstance, context.CancellationToken);
            return TakeoverReply(takeover);
        }
        catch (InvalidOperationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    private static RoomTakeoverReply TakeoverReply(RoomTakeover takeover) => new()
    {
        OwnerInstanceId = takeover.OwnerInstanceId, Epoch = takeover.Epoch, Status = takeover.Status
    };

    public override async Task<RoundStatusReply> GetRound(GetRoundRequest request, ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server", "streamer-server", "platform-gateway", "streamer-gateway");
        try
        {
            var status = await roomStore.GetRoundAsync(TenantFor(caller, request.TenantId), request.GameId,
                request.RoomId, context.CancellationToken);
            if (status is null) return new RoundStatusReply { Found = false };
            return new RoundStatusReply
            {
                Found = true, RoomId = status.RoomId, Status = status.Status, Epoch = status.Epoch,
                LastTick = status.LastTick, StateVersion = status.StateVersion,
                Participants =
                {
                    status.Participants.Select(participant => new RoundParticipantStateProto
                    {
                        ParticipantId = participant.ParticipantId, BindingId = participant.BindingId,
                        Role = participant.Role, State = participant.State
                    })
                }
            };
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    private static RoundAdmissionReply Reply(RoundAdmission admission) => new()
    {
        RoomId = admission.RoomId, RoundGeneration = admission.RoundGeneration,
        Status = admission.Status, Epoch = admission.Epoch, Duplicate = admission.Duplicate
    };

    /// <summary>Empty names the certificate tenant; otherwise a cluster scope may name any tenant.</summary>
    private static string TenantFor(ProcessIdentity caller, string requested)
    {
        if (string.IsNullOrEmpty(requested)) return caller.TenantId;
        if (caller.TenantId != "@cluster" && caller.TenantId != requested)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        return requested;
    }

    internal ProcessIdentity Require(ServerCallContext context, params string[] roles)
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

/// <summary>
/// The routing half of the M2 input boundary: platform events move into round inboxes and
/// streamer commands enter them directly. Roles follow the same split as the store.
/// </summary>
public sealed class RoundInputGrpcService(
    RedesignRoundInputStore inputStore, TrustedProcessCertificates trustedCertificates)
    : RoundInput.RoundInputBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "game-server", "streamer-server", "platform-gateway",
            "streamer-gateway");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<ParticipationReply> WithdrawParticipation(
        WithdrawParticipationRequest request, ServerCallContext context)
    {
        var caller = Require(context, "streamer-server", "game-server", "streamer-gateway");
        try
        {
            var withdrawn = await inputStore.WithdrawParticipationAsync(TenantFor(caller, request.TenantId),
                request.GameId, request.SessionId, request.RoomId, request.OperationId,
                context.CancellationToken);
            return new ParticipationReply
            {
                RoomId = withdrawn.RoomId, State = withdrawn.State, Duplicate = withdrawn.Duplicate
            };
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<RouteEventsReply> RouteEvents(RouteEventsRequest request,
        ServerCallContext context)
    {
        Require(context, "platform-gateway", "game-server", "control-server");
        try
        {
            var result = await inputStore.RouteEventsAsync(request.Platform, request.AppId,
                request.OperationId, request.MaxEvents, context.CancellationToken);
            return new RouteEventsReply
            {
                Duplicate = result.Duplicate,
                Events =
                {
                    result.Events.Select(item => new RoutedEventProto
                    {
                        EventId = item.EventId, RoomId = item.RoomId ?? "",
                        Sequence = item.Sequence ?? 0, Disposition = item.Disposition
                    })
                }
            };
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ClientInputReply> AcceptClientInput(ClientInputRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "streamer-server", "game-server", "streamer-gateway");
        try
        {
            var receipt = await inputStore.AcceptClientInputAsync(TenantFor(caller, request.TenantId),
                request.GameId, request.SessionId, request.RoomId, request.RoomEpoch, request.CommandId,
                request.PayloadJson, context.CancellationToken);
            return new ClientInputReply
            {
                CommandId = receipt.CommandId, RoomId = receipt.RoomId, Sequence = receipt.Sequence,
                RouteVersion = receipt.RouteVersion, Duplicate = receipt.Duplicate
            };
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The room epoch moved on."));
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The command id was reused for a different event."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    /// <summary>
    /// FIX-INPUT-01, durable leg: the game host forwards a directly pushed room event. The
    /// stale-owner refusal is FailedPrecondition (retry after re-resolving routing); an
    /// attribution error — the binding does not point at this room, the room will not take
    /// input — is InvalidArgument, because retrying the same attribution cannot succeed.
    /// </summary>
    public override async Task<AcceptRoomEventReply> AcceptRoomEvent(AcceptRoomEventRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "game-server", "control-server");
        try
        {
            var receipt = await inputStore.AcceptRoomEventAsync(TenantFor(caller, request.TenantId),
                request.GameId, request.RoomId, request.OwnerInstanceId, request.Epoch,
                request.EventId, request.Platform, request.AppId, request.BindingId,
                request.PayloadJson, context.CancellationToken);
            return new AcceptRoomEventReply
            {
                EventId = receipt.EventId, RoomId = receipt.RoomId, Sequence = receipt.Sequence,
                RouteVersion = receipt.RouteVersion, Duplicate = receipt.Duplicate
            };
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The room ownership fence rejected the caller."));
        }
        catch (RoundAdmissionException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The event id was reused for a different event."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

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

    /// <summary>Empty names the certificate tenant; otherwise a cluster scope may name any tenant.</summary>
    private static string TenantFor(ProcessIdentity caller, string requested)
    {
        if (string.IsNullOrEmpty(requested)) return caller.TenantId;
        if (caller.TenantId != "@cluster" && caller.TenantId != requested)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        return requested;
    }
}
