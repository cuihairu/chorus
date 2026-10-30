using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// The entity takeover boundary (architecture 10.3): the control plane moves a live entity's
/// authority to a new instance once the current holder went silent. Rooms: TransferOwnership
/// bumps both epochs in one transaction and parks the room Recovering, so the old owner's
/// frame and snapshot writes are fenced from that instant; TakeoverRoom completes it — the
/// target recovers under the Recovering fence, pins the verified head through the constrained
/// recovery entry and executes the fenced activation itself (FIX-TAKE-03), so a failed
/// recovery leaves the ownership Recovering instead of a ready room nothing runs. Streamer
/// entities: the handover is a single control-plane transaction, and the completion is
/// client-driven — the streamer resumes through the new instance, whose own claim confirms
/// the row (same holder, live lease keeps the epoch) while session rotation fences everything
/// the displaced instance might still say. Other kinds refuse visibly until their recovery
/// path lands.
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
        // Room transfers belong to a named recovery operation (FIX-TAKE-04): a lost reply
        // replays by this id instead of bumping the epoch a second time.
        if (request.Kind == "room" && string.IsNullOrWhiteSpace(request.OperationId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "A room transfer names the recovery operation it belongs to."));
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
            LeaseSeconds = request.LeaseSeconds, OperationId = request.OperationId
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
        // FIX-TAKE-03: no Active confirm before the recovery. The target recovers under the
        // room's Recovering fence — it pins the verified head through the constrained
        // recovery entry and then executes the fenced activation itself — so a recovery
        // that fails leaves the ownership Recovering instead of an Active room nothing runs.
        var ownership = await controlStore.GetOwnershipAsync(
            request.TenantId, request.GameId, "room", request.RoomId, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The room has no ownership row to recover."));
        if (ownership.OwnerInstanceId != request.ToInstanceId)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The room is held by '{ownership.OwnerInstanceId}', not '{request.ToInstanceId}'."));
        var recovered = await new GameHost.GameHostClient(channel).RecoverRoomAsync(
            new RecoverRoomRequest
            {
                RoomId = request.RoomId, GameId = request.GameId,
                OwnerInstanceId = request.ToInstanceId, Epoch = ownership.Epoch, TickMs = request.TickMs
            }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        // The data plane, not the recoverer's word, decides what the reply may claim: a
        // reported success must be an Active ownership under the takeover target.
        var after = await controlStore.GetOwnershipAsync(
            request.TenantId, request.GameId, "room", request.RoomId, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The room's ownership vanished during recovery."));
        if (recovered.Recovered && after.Status != "Active")
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The recovery reported success but the ownership is {after.Status}."));
        // FIX-TAKE-04: the open operation records the outcome — a control-plane restart
        // reads the phase instead of inferring it from lost RPC replies. A timed-out
        // recovery never reaches here, so the operation stays open for the retry.
        await controlStore.CompleteRoomRecoveryAsync(request.TenantId, request.GameId, request.RoomId,
            recovered.Recovered, recovered.LastTick, recovered.Recovered ? null : recovered.Note,
            context.CancellationToken);
        return new TakeoverRoomReply
        {
            OwnerInstanceId = after.OwnerInstanceId, Epoch = after.Epoch,
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
