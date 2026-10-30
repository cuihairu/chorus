using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.DataServer;

/// <summary>
/// Durable platform event intake (architecture 6.1). The gateway presents a verified webhook
/// batch; every reply reflects a committed batch — events, dedup keys and the operation
/// receipt are written in one transaction before the ack leaves this host.
/// </summary>
public sealed class PlatformIngestGrpcService(
    RedesignInputEventStore eventStore, TrustedProcessCertificates trustedCertificates)
    : PlatformIngest.PlatformIngestBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "platform-gateway");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    public override async Task<IngestBatchReply> IngestBatch(IngestBatchRequest request, ServerCallContext context)
    {
        Require(context, "platform-gateway");
        try
        {
            var batch = request.Events.Select(item => new PlatformEventInput(
                item.SourceDedupKey, item.Durability, item.CanonicalRoomKey, item.PayloadJson)).ToList();
            var result = await eventStore.IngestAsync(request.Platform, request.AppId, request.OperationId,
                batch, context.CancellationToken);
            return new IngestBatchReply
            {
                Duplicate = result.Duplicate,
                Receipts =
                {
                    result.Receipts.Select(receipt => new EventReceipt
                    {
                        SourceDedupKey = receipt.SourceDedupKey, Duplicate = receipt.Duplicate,
                        Disposition = receipt.Disposition, BindingId = receipt.BindingId ?? "",
                        BindingGeneration = receipt.BindingGeneration ?? 0
                    })
                }
            };
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The operation was retried with a different request."));
        }
        catch (ArgumentException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

    public override async Task<PendingInboxReply> GetPendingInbox(PendingInboxRequest request,
        ServerCallContext context)
    {
        Require(context, "platform-gateway");
        var pending = await eventStore.GetPendingAsync(request.Platform, context.CancellationToken);
        return new PendingInboxReply
        {
            Events =
            {
                pending.Select(item => new PendingInboxEvent
                {
                    SourceDedupKey = item.SourceDedupKey, CanonicalRoomKey = item.CanonicalRoomKey,
                    PayloadJson = item.PayloadJson, ReceivedAtUnixMs = item.ReceivedAtUtc.ToUnixTimeMilliseconds()
                })
            }
        };
    }

    public override async Task<ReconcileInboxReply> ReconcileInbox(ReconcileInboxRequest request,
        ServerCallContext context)
    {
        Require(context, "platform-gateway");
        var attributed = await eventStore.ReconcilePendingAsync(request.Platform, request.CanonicalRoomKey,
            context.CancellationToken);
        return new ReconcileInboxReply { Attributed = attributed };
    }

    private void Require(ServerCallContext context, string role)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!StringComparer.Ordinal.Equals(caller.Role, role))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
    }
}
