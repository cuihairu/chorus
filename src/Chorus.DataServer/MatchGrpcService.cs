using Chorus.Data;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// Match reservation boundary (architecture 7.2, 10.1). Only the control plane reserves,
/// renews, prepares and sweeps: a coordinator carrying a stale term is refused visibly, and
/// an expired reservation is retired with everything it held (AT11).
/// </summary>
public sealed class MatchGrpcService(
    RedesignMatchReservationStore reservationStore, TrustedProcessCertificates trustedCertificates)
    : Match.MatchBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<ReservationReply> ReserveMatch(ReserveMatchRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server");
        try
        {
            var reservation = await reservationStore.ReserveAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.PartitionKey,
                request.CoordinatorTerm, request.HolderInstanceId, request.RoomId,
                request.Participants.Select(Participant).ToArray(), request.ReservationSeconds,
                request.OperationId, context.CancellationToken);
            return Reply(reservation, found: true);
        }
        catch (RedesignStaleTermException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The term is no longer held by this coordinator."));
        }
        catch (RedesignCapacityException)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "The node is at its active room capacity."));
        }
        catch (RedesignMatchException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ReservationReply> RenewReservation(RenewReservationRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server");
        try
        {
            var reservation = await reservationStore.RenewReservationAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.ReservationId,
                request.CoordinatorTerm, request.HolderInstanceId, request.ReservationSeconds,
                request.OperationId, context.CancellationToken);
            return Reply(reservation, found: true);
        }
        catch (RedesignStaleTermException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The term is no longer held by this coordinator."));
        }
        catch (RedesignMatchException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ReservationReply> MarkPrepared(MarkPreparedRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server");
        try
        {
            var reservation = await reservationStore.MarkPreparedAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.ReservationId,
                request.CoordinatorTerm, request.HolderInstanceId, request.PreparedReceiptJson,
                request.OperationId, context.CancellationToken);
            return Reply(reservation, found: true);
        }
        catch (RedesignStaleTermException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The term is no longer held by this coordinator."));
        }
        catch (RedesignMatchException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ReservationReply> CommitMatch(CommitMatchRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server");
        try
        {
            var reservation = await reservationStore.CommitMatchAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.ReservationId,
                request.CoordinatorTerm, request.HolderInstanceId, request.RoomInstanceId,
                request.RoomEpoch, request.OperationId, context.CancellationToken);
            return Reply(reservation, found: true);
        }
        catch (RedesignStaleTermException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The term is no longer held by this coordinator."));
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The room is fenced by another owner."));
        }
        catch (RedesignMatchException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ReservationReply> AbortReservation(AbortReservationRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server");
        try
        {
            var reservation = await reservationStore.AbortReservationAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.ReservationId,
                request.CoordinatorTerm, request.HolderInstanceId, request.OperationId,
                context.CancellationToken);
            return Reply(reservation, found: true);
        }
        catch (RedesignStaleTermException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The term is no longer held by this coordinator."));
        }
        catch (RedesignMatchException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ReservationReply> GetReservation(GetReservationRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "control-server");
        try
        {
            var reservation = await reservationStore.GetReservationAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.ReservationId,
                context.CancellationToken);
            return reservation is null
                ? new ReservationReply { Found = false }
                : Reply(reservation, found: true);
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ExpireReservationsReply> ExpireReservations(
        ExpireReservationsRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        try
        {
            var expired = await reservationStore.ExpireReservationsAsync(request.Max,
                context.CancellationToken);
            return new ExpireReservationsReply { Expired = expired };
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ListActiveReservationsReply> ListActiveReservations(
        ListActiveReservationsRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        try
        {
            var reservations = await reservationStore.ListActiveReservationsAsync(
                request.PartitionKey, request.Max, context.CancellationToken);
            return new ListActiveReservationsReply
            {
                Reservations = { reservations.Select(ReservationProto) }
            };
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ListPartitionsReply> ListReservationPartitions(
        Empty request, ServerCallContext context)
    {
        Require(context, "control-server");
        var partitions = await reservationStore.ListReservationPartitionsAsync(context.CancellationToken);
        return new ListPartitionsReply { PartitionKeys = { partitions } };
    }

    private static ReservedParticipant Participant(ReservedParticipantProto participant) => new(
        participant.BindingId, participant.ParticipantId, participant.Role);

    private static string TenantFor(ProcessIdentity caller, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Tenant is required."));
        if (caller.TenantId != "@cluster" && caller.TenantId != requested)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        return requested;
    }

    private static ReservationReply Reply(MatchReservation reservation, bool found) => new()
    {
        Found = found, Duplicate = reservation.Duplicate, Reservation = ReservationProto(reservation)
    };

    private static ReservationProto ReservationProto(MatchReservation reservation) => new()
    {
        ReservationId = reservation.ReservationId, RoomId = reservation.RoomId,
        TenantId = reservation.TenantId, GameId = reservation.GameId,
        PartitionKey = reservation.PartitionKey, CoordinatorTerm = reservation.CoordinatorTerm,
        ExpiresUnixMs = new DateTimeOffset(reservation.ExpiresAtUtc).ToUnixTimeMilliseconds(),
        Status = reservation.Status,
        Participants = { reservation.Participants.Select(participant => new ReservedParticipantProto
        {
            BindingId = participant.BindingId, ParticipantId = participant.ParticipantId,
            Role = participant.Role
        }) },
        PreparedReceiptJson = reservation.PreparedReceiptJson ?? ""
    };

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
