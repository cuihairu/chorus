using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// The entity takeover boundary (architecture 10.3): the control plane moves a live entity's
/// authority to a new instance once the current holder went silent. Rooms: TransferOwnership
/// bumps both epochs in one transaction and parks the room Recovering, so the old owner's
/// frame and snapshot writes are fenced from that instant; TakeoverRoom completes it — confirm
/// flips the ownership Active under the new holder, then the new instance recovers from the
/// head at the bumped epoch and resumes ticking. Streamer entities: the handover is a single
/// control-plane transaction, and the completion is client-driven — the streamer resumes
/// through the new instance, whose own claim confirms the row (same holder, live lease keeps
/// the epoch) while session rotation fences everything the displaced instance might still say.
/// Other kinds refuse visibly until their recovery path lands.
/// </summary>
public sealed class TakeoverGrpcService(
    ClusterDirectory directory, RedesignControlStore controlStore,
    TrustedProcessCertificates trustedCertificates) : Takeover.TakeoverBase
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly RoundStore.RoundStoreClient _rounds = new(directory.DataChannel);

    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<TransferOwnershipReply> TransferOwnership(
        TransferOwnershipRequest request, ServerCallContext context)
    {
        Require(context, "control-server");
        if (request.Kind is not ("room" or "streamer"))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Takeover of kind '{request.Kind}' is not supported yet."));
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.EntityId) || string.IsNullOrWhiteSpace(request.ToInstanceId) ||
            request.LeaseSeconds is <= 0 or > 60)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The takeover request is incomplete."));
        // The control plane is the authority: the handover names the current holder so a stale
        // command cannot move an entity that already changed hands.
        var ownership = await controlStore.GetOwnershipAsync(
            request.TenantId, request.GameId, request.Kind, request.EntityId, context.CancellationToken);
        if (ownership is not { Status: "Active" })
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The entity ownership is not Active; it cannot transfer."));
        if (request.Kind == "streamer")
        {
            try
            {
                var moved = await controlStore.TransferOwnershipAsync(request.TenantId, request.GameId,
                    "streamer", request.EntityId, ownership.OwnerInstanceId, request.ToInstanceId,
                    request.LeaseSeconds, context.CancellationToken);
                return new TransferOwnershipReply
                {
                    OwnerInstanceId = moved.OwnerInstanceId, Epoch = moved.Epoch, Status = moved.Status
                };
            }
            catch (InvalidOperationException error)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
            }
        }
        var takeover = await _rounds.TransferRoomOwnershipAsync(new TransferRoomOwnershipRequest
        {
            TenantId = request.TenantId, GameId = request.GameId, RoomId = request.EntityId,
            FromInstance = ownership.OwnerInstanceId, ToInstance = request.ToInstanceId,
            LeaseSeconds = request.LeaseSeconds
        }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        return new TransferOwnershipReply
        {
            OwnerInstanceId = takeover.OwnerInstanceId, Epoch = takeover.Epoch, Status = takeover.Status
        };
    }

    public override async Task<TakeoverRoomReply> TakeoverRoom(TakeoverRoomRequest request,
        ServerCallContext context)
    {
        Require(context, "control-server");
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.ToInstanceId) ||
            request.TickMs <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The takeover request is incomplete."));
        var channel = directory.GameHostChannel(request.ToInstanceId)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"The takeover target '{request.ToInstanceId}' is not a known game host."));
        // Confirm first: the fence rejects the new owner too while Recovering, so the recovery
        // that follows must run under an Active ownership at the bumped epoch.
        var confirm = await _rounds.ConfirmRoomOwnershipAsync(new ConfirmRoomOwnershipRequest
        {
            TenantId = request.TenantId, GameId = request.GameId, RoomId = request.RoomId,
            OwnerInstance = request.ToInstanceId
        }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        var recovered = await new GameHost.GameHostClient(channel).RecoverRoomAsync(
            new RecoverRoomRequest
            {
                RoomId = request.RoomId, GameId = request.GameId,
                OwnerInstanceId = request.ToInstanceId, Epoch = confirm.Epoch, TickMs = request.TickMs
            }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        return new TakeoverRoomReply
        {
            OwnerInstanceId = request.ToInstanceId, Epoch = confirm.Epoch,
            Recovered = recovered.Recovered, Status = recovered.Status, LastTick = recovered.LastTick
        };
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
