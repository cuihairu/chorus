using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.StreamerServer;

/// <summary>
/// The participation-commit face (FIX-LINK-04, architecture 7.2 step 6): the control plane's
/// dispatcher pushes a mount's round.started commit here, and the hosted streamer's in-memory
/// binding facts are filled with the room its round pointer advanced to — the mount binds the
/// new room without waiting for a poll. The handler is idempotent: an absent actor (the owner
/// was resolved from process reports, so this is a recovery window the query fallback covers)
/// or a stale generation is a no-op, never an error, so at-least-once delivery converges.
/// </summary>
public sealed class ParticipationNotifyGrpcService(
    StreamerActorRegistry actorRegistry, TrustedProcessCertificates trustedCertificates)
    : Participation.ParticipationBase
{
    public override async Task<NotifyCommittedReply> NotifyCommitted(
        NotifyCommittedRequest request, ServerCallContext context)
    {
        var caller = Require(context);
        if (!StringComparer.Ordinal.Equals(caller.TenantId, "@cluster") &&
            !StringComparer.Ordinal.Equals(caller.TenantId, request.TenantId))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant is not authorized."));
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.BindingId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The participation notice is incomplete."));
        if (request.RoundGeneration <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The round generation must be a positive fact."));
        // The actor's own binding gate decides relevance (binding + mount generation match,
        // generation strictly newer) — the notice is applied only when it describes the mount.
        var actor = actorRegistry.Find(request.TenantId, request.StreamerId);
        if (actor is not null)
            await actor.OnParticipationCommittedAsync(request.BindingId, request.BindingGeneration,
                request.RoomId, request.RoundGeneration, context.CancellationToken);
        return new NotifyCommittedReply { Applied = true };
    }

    private ProcessIdentity Require(ServerCallContext context)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!StringComparer.Ordinal.Equals(caller.Role, "control-server"))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
        return caller;
    }
}
