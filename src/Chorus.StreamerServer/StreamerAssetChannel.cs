using System.Text.Json;
using Chorus.Protocol;
using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerServer;

/// <summary>The result of an asset change: the stored balance/version after the write, and whether
/// the operation was an idempotent replay of an already-committed change (m0 AssetReply).</summary>
public sealed record AssetChangeResult(long Balance, long Version, bool Duplicate);

/// <summary>
/// FIX-LINK-07: an asset change that would drive the balance negative (m0 <c>ChangeAsset</c> CAS
/// constraint "balance + delta &gt;= 0"). The caller decides whether to reject or reload.
/// </summary>
public sealed class StreamerAssetConflictException : Exception;

/// <summary>
/// Entity asset access through the DataServer. FIX-LINK-07 adds the m0 versioned/idempotent write
/// surface — the StreamerServer is the designated <c>DataService.Get/ChangeAsset</c> client (m0
/// contract). This is write-only: the business handler's balance read for rule validation belongs
/// to FIX-GM-07 (the GM command that triggers the change).
/// </summary>
public sealed class StreamerAssetChannel(Func<GrpcChannel> channelFactory) : IStreamerAssetStore
{
    public async Task<AssetChangeResult> ChangeAsync(string tenantId, string gameId, string streamerId,
        string operationId, string assetId, long delta, string reason,
        CancellationToken cancellationToken = default)
    {
        using var channel = channelFactory();
        var client = new DataService.DataServiceClient(channel);
        try
        {
            // The request fingerprint must be byte-identical to what DataServer computes from
            // its own request ({ AssetId, Delta, Reason }), so the canonical object here mirrors
            // that shape exactly (same names, same order).
            var fingerprint = OperationFingerprint.FromCanonicalRequest(1,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    AssetId = assetId, Delta = delta, Reason = reason
                }));
            var reply = await client.ChangeAssetAsync(new ChangeAssetRequest
            {
                Key = new OperationKey
                {
                    TenantId = tenantId, GameId = gameId, OperationKind = "asset.change",
                    TargetId = streamerId, OperationId = operationId, CanonicalVersion = 1,
                    RequestHashHex = fingerprint.Sha256Hex
                },
                AssetId = assetId, Delta = delta, Reason = reason
            }, cancellationToken: cancellationToken);
            // Deliberate deviation from PatchAsync (which drops Duplicate): here Duplicate must
            // surface — AT18's idempotent asset transaction and the wire replay fact depend on it.
            return new AssetChangeResult(reply.Balance, reply.Version, reply.Duplicate);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Aborted)
        {
            // m0 surfaces the balance constraint as Aborted; keep gRPC codes out of the business layer.
            throw new StreamerAssetConflictException();
        }
    }
}
