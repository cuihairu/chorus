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
    RedesignFrameStore frameStore, RedesignRecoveryStore recoveryStore,
    TrustedProcessCertificates trustedCertificates)
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
                        grant.PlayerId, grant.GrantKey, grant.OperationId, grant.AssetId,
                        grant.Delta)).ToArray()),
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
                        BindingId = item.BindingId ?? "", PayloadJson = item.PayloadJson,
                        Platform = item.Platform, SourceDedupKey = item.SourceDedupKey
                    })
                }
            };
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<SnapshotReceiptReply> PutSnapshot(PutSnapshotRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "game-server");
        try
        {
            var receipt = await recoveryStore.PutSnapshotAsync(caller.TenantId, request.GameId,
                request.RoomId, request.OwnerInstanceId, request.Epoch, request.Tick,
                request.SnapshotPayload.ToByteArray(), request.SnapshotHash.ToByteArray(),
                request.SnapshotSchemaVersion, request.OperationId, context.CancellationToken);
            return new SnapshotReceiptReply
            {
                RoomId = receipt.RoomId, Tick = receipt.Tick,
                SnapshotSchemaVersion = receipt.SchemaVersion, Verified = receipt.Verified,
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different snapshot."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<SnapshotReceiptReply> PutRecoverySnapshot(PutRecoverySnapshotRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "game-server");
        try
        {
            var receipt = await recoveryStore.PutRecoverySnapshotAsync(caller.TenantId, request.GameId,
                request.RoomId, request.OwnerInstanceId, request.Epoch, request.Tick,
                request.SnapshotPayload.ToByteArray(), request.SnapshotHash.ToByteArray(),
                request.SnapshotSchemaVersion, request.OperationId, context.CancellationToken);
            return new SnapshotReceiptReply
            {
                RoomId = receipt.RoomId, Tick = receipt.Tick,
                SnapshotSchemaVersion = receipt.SchemaVersion, Verified = receipt.Verified,
                Duplicate = receipt.Duplicate
            };
        }
        catch (RedesignStaleOwnerException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The recovery snapshot fence rejected the caller."));
        }
        catch (FrameConflictException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different snapshot."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<LoadRecoveryReply> LoadRecovery(LoadRecoveryRequest request,
        ServerCallContext context)
    {
        var caller = Require(context, "game-server", "control-server");
        try
        {
            var depth = request.CandidateDepth > 0 ? request.CandidateDepth
                : RedesignRecoveryStore.CandidateDepth;
            var plan = await recoveryStore.LoadRecoveryAsync(caller.TenantId, request.GameId,
                request.RoomId, depth, context.CancellationToken);
            if (plan is null) return new LoadRecoveryReply { Found = false };
            // FIX-READ-05: one bounded page of the plan per reply. A continuation re-reads
            // the plan and refuses to continue a read whose view moved — pages are never
            // stitched from two heads. Cancelling is simply not asking for the next page.
            RecoveryPageToken cursor;
            if (request.PageToken.Length == 0)
            {
                cursor = new RecoveryPageToken(depth, plan.Epoch, plan.LastTick, 0, 0);
            }
            else
            {
                cursor = RecoveryPager.Decode(request.PageToken.Span)
                    ?? throw new ArgumentException("The page token is not a recovery cursor.");
                if (cursor.Depth != depth || cursor.Epoch != plan.Epoch || cursor.Head != plan.LastTick)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition,
                        "The recovery view moved; restart the read."));
            }
            var (page, next) = RecoveryPager.TakePage(plan, cursor);
            var reply = new LoadRecoveryReply
            {
                Found = true, RoomId = page.RoomId, Status = page.Status, Epoch = page.Epoch,
                LastTick = page.LastTick, StateVersion = page.StateVersion,
                Snapshots =
                {
                    page.Snapshots.Select(snapshot => new RecoverySnapshotProto
                    {
                        Tick = snapshot.Tick, SchemaVersion = snapshot.SchemaVersion,
                        Payload = Google.Protobuf.ByteString.CopyFrom(snapshot.Payload),
                        PayloadHash = Google.Protobuf.ByteString.CopyFrom(snapshot.PayloadHash),
                        Verified = snapshot.Verified
                    })
                },
                Deliveries =
                {
                    page.Deliveries.Select(delivery => new RecoveryDeliveryProto
                    {
                        EventId = delivery.EventId, Sequence = delivery.Sequence,
                        BindingId = delivery.BindingId ?? "", PayloadJson = delivery.PayloadJson,
                        Consumed = delivery.Consumed
                    })
                },
                Frames =
                {
                    page.Frames.Select(frame => new RecoveryFrameProto
                    {
                        Tick = frame.Tick, StateHash = Google.Protobuf.ByteString.CopyFrom(frame.StateHash),
                        ModuleVersion = frame.ModuleVersion, ConfigHash = frame.ConfigHash,
                        InputEventIds = { frame.InputEventIds }
                    })
                },
                Participants =
                {
                    page.Participants.Select(participant => new RoundParticipantStateProto
                    {
                        ParticipantId = participant.ParticipantId, BindingId = participant.BindingId,
                        Role = participant.Role, State = participant.State
                    })
                }
            };
            if (next is not null)
                reply.NextPageToken = Google.Protobuf.ByteString.CopyFrom(RecoveryPager.Encode(next));
            return reply;
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
