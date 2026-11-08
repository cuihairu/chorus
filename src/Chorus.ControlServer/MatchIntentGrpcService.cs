using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// The control plane's match-intent entry (FIX-LINK-03, architecture 6.4): the StreamerServer
/// business path submits a mount's intent here, the control plane verifies the mount is the
/// registered route source (FIX-END-02) before forwarding the ticket to the queue the
/// coordinator scans. The queue stays the data plane's — this face adds the registration gate,
/// not storage. The gateway's client path keeps calling MatchQueue directly (architecture 143
/// names the business path; FIX-LINK-08 reconciles the two faces). The exit face (FIX-LINK-12)
/// cancels a mount's queued ticket under the same gate.
/// </summary>
public sealed class MatchIntentGrpcService(
    RedesignControlStore store, ClusterDirectory directory,
    TrustedProcessCertificates trustedCertificates) : MatchIntent.MatchIntentBase
{
    public override async Task<TicketReply> SubmitMatchIntent(
        SubmitMatchIntentRequest request, ServerCallContext context)
    {
        var caller = Require(context, "streamer-server");
        if (caller.TenantId != "@cluster" && caller.TenantId != request.TenantId)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.BindingId) || string.IsNullOrWhiteSpace(request.SessionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The match intent is incomplete."));
        if (request.BindingGeneration <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The binding generation must be a positive fact."));
        // The gate is the FIX-END-02 registration: the mount the intent names must be the
        // registered route source at its current generation — the streamer is the row's, never
        // a caller field.
        try
        {
            await store.ValidateMatchIntentSourceAsync(request.TenantId, request.GameId,
                request.BindingId, request.BindingGeneration, context.CancellationToken);
        }
        catch (RouteSourceUnverifiedException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (StaleBindingGenerationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        return await new MatchQueue.MatchQueueClient(directory.DataChannel).EnqueueTicketAsync(
            new EnqueueTicketRequest
            {
                TenantId = request.TenantId, GameId = request.GameId, BindingId = request.BindingId,
                SessionId = request.SessionId, PartitionKey = request.PartitionKey,
                OperationId = request.OperationId, ExpiresUnixMs = request.ExpiresUnixMs
            }, cancellationToken: context.CancellationToken);
    }

    /// <summary>
    /// The exit mirror of the submit (FIX-LINK-12): the same gates — role, tenant fence, route
    /// source — then the cancel forwards to the queue. The mount named must still be the
    /// registered route source at its current generation: a superseded mount cannot cancel the
    /// new mount's ticket, and an unregistered mount never established an intent to cancel.
    /// Whether the ticket can actually cancel stays the data plane's answer (only a Queued one
    /// does); this face never guesses it.
    /// </summary>
    public override async Task<TicketReply> CancelMatchIntent(
        CancelMatchIntentRequest request, ServerCallContext context)
    {
        var caller = Require(context, "streamer-server");
        if (caller.TenantId != "@cluster" && caller.TenantId != request.TenantId)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.BindingId) || string.IsNullOrWhiteSpace(request.OperationId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The match intent is incomplete."));
        if (request.BindingGeneration <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The binding generation must be a positive fact."));
        try
        {
            await store.ValidateMatchIntentSourceAsync(request.TenantId, request.GameId,
                request.BindingId, request.BindingGeneration, context.CancellationToken);
        }
        catch (RouteSourceUnverifiedException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (StaleBindingGenerationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        return await new MatchQueue.MatchQueueClient(directory.DataChannel).CancelTicketAsync(
            new CancelTicketRequest
            {
                TenantId = request.TenantId, GameId = request.GameId, BindingId = request.BindingId,
                OperationId = request.OperationId
            }, cancellationToken: context.CancellationToken);
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
}
