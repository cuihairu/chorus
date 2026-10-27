using Chorus.Data.Redesign;
using Chorus.DataServer;
using Chorus.Redesign.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// The frame boundary (architecture 8.2): only the room owner commits ticks; the owner and
/// the control plane read the round inbox. The tenant is the caller's certificate tenant and
/// one commit is one store transaction, so a reply always reflects a committed frame.
/// </summary>
public sealed class FrameGrpcService(
    RedesignFrameStore frameStore, TrustedProcessCertificates trustedCertificates)
    : FrameStore.FrameStoreBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<FrameReceiptReply> CommitFrame(CommitFrameRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "game-server");
        try
        {
            var receipt = await frameStore.CommitFrameAsync(caller.TenantId, request.GameId, request.RoomId,
                new FrameCommit(
                    request.CommitId, request.OwnerInstanceId, request.Epoch, request.PrevTick,
                    request.PrevStateVersion, request.InputEventIds.ToArray(), request.StateHash.ToByteArray(),
                    request.Effects.Select(effect => new FrameEffect(effect.Kind, effect.PayloadJson)).ToArray(),
                    request.ModuleVersion, request.ConfigHash,
                    request.SnapshotPayload.Length > 0 ? request.SnapshotPayload.ToByteArray() : null,
                    request.SnapshotHash.Length > 0 ? request.SnapshotHash.ToByteArray() : null,
                    request.SnapshotSchemaVersion,
                    request.Final, request.ResultJson.Length > 0 ? request.ResultJson : null,
                    request.PlanHash.Length > 0 ? request.PlanHash.ToByteArray() : null,
                    request.Grants.Select(grant => new PlannedGrant(
                        grant.PlayerId, grant.GrantKey, grant.OperationId)).ToArray()),
                context.CancellationToken);
            return new FrameReceiptReply
            {
                CommitId = receipt.CommitId, RoomId = receipt.RoomId, Tick = receipt.Tick,
                StateVersion = receipt.StateVersion, Watermark = receipt.Watermark,
                Duplicate = receipt.Duplicate
            };
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The room ownership fence rejected the caller."));
        }
        catch (FrameConflictException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The commit id was retried with a different frame."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<ReadInboxReply> ReadInbox(ReadInboxRequest request, ServerCallContext context)
    {
        var caller = Require(context, "game-server", "control-server");
        try
        {
            var inbox = await frameStore.ReadInboxAsync(caller.TenantId, request.GameId, request.RoomId,
                request.MaxEvents, context.CancellationToken);
            return new ReadInboxReply
            {
                Watermark = inbox.Watermark,
                Items =
                {
                    inbox.Items.Select(item => new InboxItemProto
                    {
                        EventId = item.EventId, Sequence = item.Sequence, RouteVersion = item.RouteVersion,
                        BindingId = item.BindingId ?? "", PayloadJson = item.PayloadJson
                    })
                }
            };
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
}
