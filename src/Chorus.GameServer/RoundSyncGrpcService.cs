using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.GameServer;

/// <summary>
/// Client sync boundary (architecture 8.3): the streamer edge subscribes to a room's
/// committed frames. The host publishes only what committed — full snapshots and deltas of
/// the authoritative record, command receipts for consumed client commands, the frozen
/// result at the end — and demands a resync when the subscriber's epoch is stale. The room
/// never waits on a subscriber; a lagging reader is re-baselined with a snapshot.
/// </summary>
public sealed class RoundSyncGrpcService(
    RoomRuntime runtime, TrustedProcessCertificates trustedCertificates)
    : RoundSync.RoundSyncBase
{
    public override async Task Subscribe(SubscribeRequest request,
        IServerStreamWriter<SyncMessage> responseStream, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.GameId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The room id and game id are required."));
        Require(context, "streamer-server", "control-server");
        var actor = runtime.FindRoom(request.RoomId);
        if (actor is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                "The room does not run on this host."));
        if (request.Epoch != actor.Epoch)
        {
            // A subscriber on a stale epoch can never validate these frames; it gets the
            // current epoch and resubscribes rather than a stream it must discard (8.3).
            await responseStream.WriteAsync(new SyncMessage
            {
                ResyncRequired = new ResyncRequiredMessage
                {
                    RoomId = request.RoomId, CurrentEpoch = actor.Epoch,
                    Reason = $"The room moved to epoch {actor.Epoch}."
                }
            }, context.CancellationToken);
            return;
        }
        var subscriber = actor.Subscribe(request.LastAppliedTick, request.LastAppliedStateVersion);
        try
        {
            while (true)
            {
                var message = await subscriber.NextAsync(context.CancellationToken);
                if (message is null) return; // the round finished and the queue drained
                await responseStream.WriteAsync(message, context.CancellationToken);
            }
        }
        finally
        {
            actor.Detach(subscriber);
            subscriber.Dispose();
        }
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
