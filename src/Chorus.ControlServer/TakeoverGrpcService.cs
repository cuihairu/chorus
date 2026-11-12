using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// The entity takeover boundary (architecture 10.3): the control plane moves a live entity's
/// authority to a new instance once the current holder went silent. Rooms: TransferOwnership
/// bumps both epochs in one transaction and parks the room Recovering, so the old owner's
/// frame and snapshot writes are fenced from that instant; TakeoverRoom completes it — the
/// target recovers under the Recovering fence and pins the verified head through the
/// constrained recovery entry, then the control plane waits for the gateways to confirm the
/// takeover's routing version and grants the activation only past that barrier
/// (FIX-REC-01): the fenced confirm flips the ownership Active for exactly this owner and
/// epoch and the loop starts. A failed recovery or an unconfirmed barrier leaves the
/// ownership Recovering instead of a ready room nothing runs. Streamer entities: the
/// handover is a single control-plane transaction, and the completion is client-driven —
/// the streamer resumes through the new instance, whose own claim confirms the row (same
/// holder, live lease keeps the epoch) while session rotation fences everything the
/// displaced instance might still say. Other kinds refuse visibly until their recovery
/// path lands.
/// </summary>
public sealed class TakeoverGrpcService(
    ClusterDirectory directory, RedesignControlStore controlStore,
    TrustedProcessCertificates trustedCertificates) : Takeover.TakeoverBase
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    // FIX-REC-01: how long the gateways get to confirm the takeover's routing version
    // before the activation is refused and the operation stays open for the retry — the
    // same window the battle-start barrier gives its gateway (FIX-END-16).
    private const int RouteBarrierTimeoutMs = 10000;

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
        return await RecoverRoomWithBarrierAsync(request.TenantId, request.GameId, request.RoomId,
            request.ToInstanceId, request.TickMs, context.CancellationToken);
    }

    /// <summary>
    /// The one room-recovery flow (architecture 10.3, doc: no second recovery
    /// implementation): the takeover and the operator's admin retry both drive it. The
    /// target recovers under the Recovering fence and holds; the control plane records the
    /// preparation, waits for the gateways to confirm the takeover's routing version, and
    /// grants the activation only past that barrier — the fenced confirm flips the
    /// ownership Active for exactly this owner and epoch, then the loop starts. A failed
    /// recovery or an unconfirmed barrier leaves the ownership Recovering and the
    /// operation open for the retry.
    /// </summary>
    internal async Task<TakeoverRoomReply> RecoverRoomWithBarrierAsync(string tenantId, string gameId,
        string roomId, string toInstanceId, int tickMs, CancellationToken cancellationToken)
    {
        var channel = directory.GameHostChannel(toInstanceId)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"The takeover target '{toInstanceId}' is not a known game host."));
        // FIX-TAKE-03: no Active confirm before the recovery. The target recovers under the
        // room's Recovering fence — it pins the verified head through the constrained
        // recovery entry and holds — so a recovery that fails leaves the ownership
        // Recovering instead of an Active room nothing runs.
        var ownership = await controlStore.GetOwnershipAsync(
            tenantId, gameId, "room", roomId, cancellationToken)
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The room has no ownership row to recover."));
        if (ownership.OwnerInstanceId != toInstanceId)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The room is held by '{ownership.OwnerInstanceId}', not '{toInstanceId}'."));
        // The activation leg keys on the open operation the transfer recorded (FIX-TAKE-04):
        // a lost ActivateRoom reply replays the same id, and a reassignment's own operation
        // id carries forward. Rooms transferred before the record landed recover on a
        // derived id.
        var open = await controlStore.GetOpenRoomRecoveryAsync(tenantId, gameId, roomId, cancellationToken);
        var operationId = open?.OperationId ?? $"takeover/{roomId}/{ownership.Epoch}";
        var gameHost = new GameHost.GameHostClient(channel);
        var recovered = await gameHost.RecoverRoomAsync(
            new RecoverRoomRequest
            {
                RoomId = roomId, GameId = gameId,
                OwnerInstanceId = toInstanceId, Epoch = ownership.Epoch, TickMs = tickMs,
                // FIX-REC-01: the recovery prepares and holds — the grant waits for the
                // route barrier, so the loop cannot outrun the routes that deliver to it.
                DeferredActivation = true
            }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken);
        if (!recovered.Recovered)
        {
            // FIX-TAKE-04: the open operation records the outcome — a control-plane restart
            // reads the phase instead of inferring it from lost RPC replies. A timed-out
            // recovery never reaches here, so the operation stays open for the retry.
            await controlStore.CompleteRoomRecoveryAsync(tenantId, gameId, roomId,
                recovered: false, recovered.LastTick, recovered.Note, cancellationToken);
            return new TakeoverRoomReply
            {
                OwnerInstanceId = ownership.OwnerInstanceId, Epoch = ownership.Epoch,
                Recovered = false, Status = recovered.Status, LastTick = recovered.LastTick
            };
        }
        if (recovered.Status == "Ready")
        {
            // FIX-REC-01: the preparation is durable progress — the phase moves to Prepared
            // with an extended deadline so the barrier wait and the activation survive the
            // transfer's lease. A restarted control plane re-enters here: the held actor
            // answers Ready again and the flow continues at the barrier, never re-pinning.
            await controlStore.MarkRoomRecoveryPreparedAsync(tenantId, gameId,
                roomId, null, cancellationToken);
            if (!await controlStore.WaitForRecoveryRoutesConfirmedAsync(roomId,
                    TimeSpan.FromMilliseconds(RouteBarrierTimeoutMs), cancellationToken))
            {
                var note = "The gateways never confirmed the route within the barrier window; " +
                           "the room stays fenced and held for the retry.";
                await controlStore.MarkRoomRecoveryPreparedAsync(tenantId, gameId,
                    roomId, note, cancellationToken);
                throw new RpcException(new Status(StatusCode.FailedPrecondition, note));
            }
            // The barrier held: the grant is the activation — the fenced confirm flips the
            // ownership Active for exactly this owner and epoch, then the loop starts. A
            // lost reply replays duplicate on the held actor.
            await gameHost.ActivateRoomAsync(new ActivateRoomRequest
            {
                RoomId = roomId, GameId = gameId,
                OwnerInstanceId = toInstanceId, Epoch = ownership.Epoch,
                OperationId = operationId + "/activate"
            }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken);
        }
        // Status "Running" means the activation already landed (a retry after a lost
        // ActivateRoom reply) — the held room started and only the record is missing.
        // The data plane, not the recoverer's word, decides what the reply may claim: a
        // reported success must be an Active ownership under the takeover target.
        var after = await controlStore.GetOwnershipAsync(
            tenantId, gameId, "room", roomId, cancellationToken)
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The room's ownership vanished during recovery."));
        if (after.Status != "Active")
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The recovery reported success but the ownership is {after.Status}."));
        await controlStore.CompleteRoomRecoveryAsync(tenantId, gameId, roomId,
            recovered: true, recovered.LastTick, null, cancellationToken);
        // The reply reports the room's post-call state, not the recoverer's hold state: a
        // deferred recovery that cleared the barrier just activated, so the room runs now.
        var replyStatus = recovered.Status == "Ready" ? "Running" : recovered.Status;
        return new TakeoverRoomReply
        {
            OwnerInstanceId = after.OwnerInstanceId, Epoch = after.Epoch,
            Recovered = true, Status = replyStatus, LastTick = recovered.LastTick
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
