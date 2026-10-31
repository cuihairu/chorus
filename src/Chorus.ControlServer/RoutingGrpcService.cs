using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// The routing face of the control plane (architecture 4.4). GetRoutingSnapshot answers with
/// the versioned full snapshot; WatchRouting emits a notice whenever the routed content
/// changes. Watch is a delivery optimization, not a reliable log — a consumer that misses a
/// notice or breaks the stream re-pulls the full snapshot, and the content-hash version makes
/// the mismatch exact. Routing carries endpoints, instances and epochs only — never secrets.
/// </summary>
public sealed class RoutingGrpcService(
    RedesignControlStore store, TrustedProcessCertificates trustedCertificates) : Routing.RoutingBase
{
    private static readonly TimeSpan WatchPollInterval = TimeSpan.FromMilliseconds(500);

    public override async Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        await RequireAsync(context, "node-agent", "data-server", "streamer-server", "game-server",
            "streamer-gateway", "platform-gateway", "control-server");
        return new ReadyReply { Ready = true, SchemaVersion = "4" };
    }

    public override async Task<RoutingSnapshotReply> GetRoutingSnapshot(Empty request, ServerCallContext context)
    {
        await RequireAsync(context, "node-agent", "data-server", "streamer-server", "game-server",
            "streamer-gateway", "platform-gateway", "control-server");
        var snapshot = await store.ReadRoutingSnapshotAsync(context.CancellationToken);
        return new RoutingSnapshotReply
        {
            Version = snapshot.Version,
            Rooms = { snapshot.Rooms.Select(room => new RoomRouteProto
            {
                RoomId = room.RoomId, TenantId = room.TenantId, GameId = room.GameId,
                OwnerInstanceId = room.OwnerInstanceId, Epoch = room.Epoch,
                Endpoint = room.Endpoint ?? "", Status = room.Status
            }) },
            Streamers = { snapshot.Streamers.Select(streamer => new StreamerRouteProto
            {
                StreamerId = streamer.StreamerId, BindingId = streamer.BindingId,
                RoomId = streamer.RoomId, State = streamer.State,
                OwnerInstanceId = streamer.OwnerInstanceId ?? "", Epoch = streamer.Epoch,
                Platform = streamer.Platform, CanonicalLiveSessionKey = streamer.CanonicalLiveSessionKey,
                BindingGeneration = streamer.BindingGeneration
            }) },
            Instances = { snapshot.Instances.Select(instance => new InstanceRouteProto
            {
                ProcessInstanceId = instance.ProcessInstanceId, Role = instance.Role,
                NodeId = instance.NodeId, SlotId = instance.SlotId,
                Endpoint = instance.Endpoint ?? "", Readiness = instance.Readiness,
                Admission = instance.Admission
            }) }
        };
    }

    /// <summary>
    /// The mount's route source (FIX-END-02): the StreamerServer reports each committed
    /// mount by binding id and generation. The control plane derives the platform identity
    /// from the mount's verified live session — an unverified identity establishes no route —
    /// and a report naming an older generation than the recorded row never overwrites it.
    /// </summary>
    public override async Task<RegisterRouteSourceReply> RegisterRouteSource(
        RegisterRouteSourceRequest request, ServerCallContext context)
    {
        await RequireAsync(context, "streamer-server", "control-server");
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.StreamerId) || string.IsNullOrWhiteSpace(request.BindingId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The route-source report is incomplete."));
        if (request.BindingGeneration <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The binding generation must be a positive fact."));
        try
        {
            var (record, duplicate) = await store.RegisterRouteSourceAsync(
                request.TenantId, request.GameId, request.StreamerId, request.BindingId,
                request.BindingGeneration, context.CancellationToken);
            return new RegisterRouteSourceReply
            {
                Platform = record.Platform, CanonicalLiveSessionKey = record.CanonicalLiveSessionKey,
                BindingGeneration = record.BindingGeneration, Duplicate = duplicate
            };
        }
        catch (RouteSourceUnverifiedException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (StaleBindingGenerationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
    }

    public override async Task WatchRouting(WatchRoutingRequest request,
        IServerStreamWriter<RoutingNotice> responseStream, ServerCallContext context)
    {
        await RequireAsync(context, "node-agent", "data-server", "streamer-server", "game-server",
            "streamer-gateway", "platform-gateway", "control-server");
        var lastVersion = request.KnownVersion;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var snapshot = await store.ReadRoutingSnapshotAsync(context.CancellationToken);
            if (snapshot.Version != lastVersion)
            {
                lastVersion = snapshot.Version;
                await responseStream.WriteAsync(new RoutingNotice { Version = snapshot.Version },
                    context.CancellationToken);
            }
            await Task.Delay(WatchPollInterval, context.CancellationToken);
        }
    }

    private async Task RequireAsync(ServerCallContext context, params string[] roles)
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
