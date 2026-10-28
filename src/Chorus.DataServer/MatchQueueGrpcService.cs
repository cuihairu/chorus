using Chorus.Data.Redesign;
using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// Match queue boundary (architecture 7.1, 7.3). Streamers and the control plane enqueue and
/// cancel; only the coordinator reads candidate windows and sweeps stale tickets. Cluster-scope
/// callers name the tenant they act for; tenant-scope callers inherit their certificate tenant.
/// </summary>
public sealed class MatchQueueGrpcService(
    RedesignMatchTicketStore ticketStore, TrustedProcessCertificates trustedCertificates)
    : MatchQueue.MatchQueueBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "streamer-server", "game-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<TicketReply> EnqueueTicket(EnqueueTicketRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "streamer-server", "control-server");
        try
        {
            var ticket = await ticketStore.EnqueueTicketAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.BindingId, request.SessionId,
                request.PartitionKey, request.OperationId,
                request.ExpiresUnixMs == 0 ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(request.ExpiresUnixMs).UtcDateTime,
                context.CancellationToken);
            return Reply(ticket, found: true);
        }
        catch (SessionRevokedException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Session is revoked."));
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

    public override async Task<TicketReply> CancelTicket(CancelTicketRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "streamer-server", "control-server");
        try
        {
            var ticket = await ticketStore.CancelTicketAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.BindingId, request.OperationId,
                context.CancellationToken);
            return Reply(ticket, found: true);
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

    public override async Task<TicketReply> GetTicket(GetTicketRequest request, ServerCallContext context)
    {
        var caller = Require(context, "streamer-server", "control-server", "game-server");
        try
        {
            var ticket = await ticketStore.GetTicketAsync(
                TenantFor(caller, request.TenantId), request.GameId, request.BindingId, context.CancellationToken);
            return ticket is null ? new TicketReply { Found = false } : Reply(ticket, found: true);
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ListQueuedReply> ListQueued(ListQueuedRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server");
        try
        {
            var tickets = await ticketStore.ListQueuedAsync(
                request.PartitionKey, request.Max, context.CancellationToken);
            return new ListQueuedReply
            {
                Tickets = { tickets.Select(TicketProto) }
            };
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ExpireQueuedReply> ExpireQueued(ExpireQueuedRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server");
        try
        {
            var expired = await ticketStore.ExpireQueuedAsync(request.Max, context.CancellationToken);
            return new ExpireQueuedReply { Expired = expired };
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ListPartitionsReply> ListPartitions(Empty request,
        ServerCallContext context)
    {
        Require(context, "control-server");
        var partitions = await ticketStore.ListPartitionKeysAsync(context.CancellationToken);
        return new ListPartitionsReply { PartitionKeys = { partitions } };
    }

    private static string TenantFor(ProcessIdentity caller, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Tenant is required."));
        if (caller.TenantId != "@cluster" && caller.TenantId != requested)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        return requested;
    }

    private static TicketReply Reply(MatchTicket ticket, bool found) => new()
    {
        Found = found, Duplicate = ticket.Duplicate, Ticket = TicketProto(ticket)
    };

    private static TicketProto TicketProto(MatchTicket ticket) => new()
    {
        TicketId = ticket.TicketId, BindingId = ticket.BindingId, State = ticket.State,
        Version = ticket.Version, PartitionKey = ticket.PartitionKey,
        ExpiresUnixMs = ticket.ExpiresAtUtc is null ? 0
            : new DateTimeOffset(ticket.ExpiresAtUtc.Value).ToUnixTimeMilliseconds(),
        ReservationId = ticket.ReservationId ?? "", StreamerId = ticket.StreamerId,
        TenantId = ticket.TenantId, GameId = ticket.GameId
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
