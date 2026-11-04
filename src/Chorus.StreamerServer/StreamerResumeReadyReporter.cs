using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;
using Microsoft.Extensions.Logging;

namespace Chorus.StreamerServer;

/// <summary>
/// FIX-SYNC-08: the readiness signal source (authoritative-sync.md:53). A control participant
/// whose session participates in a paused round cannot re-mount — the mount is refused while
/// the round is paused — but the session stands at the current generation on that paused round,
/// which is exactly the resume-ready declaration. This reporter is fire-and-forget: a lost or
/// refused report never blocks the mount refusal it accompanies, and the room stays Paused for
/// the next report (a participant that never re-mounts is simply not ready, and the pause times
/// out by the module's rule). The store resolves the paused room from the streamer's open live
/// session and fences the report on the session being current, so an old session's readiness
/// never counts.
/// </summary>
public sealed class StreamerResumeReadyReporter(Func<GrpcChannel> channelFactory,
    ILogger<StreamerResumeReadyReporter> logger)
{
    public async Task ReportResumeReadyAsync(string tenantId, string gameId, string streamerId,
        string sessionId, string operationId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var channel = channelFactory();
            var client = new RoundStore.RoundStoreClient(channel);
            await client.ReportResumeReadyAsync(new ReportResumeReadyRequest
            {
                TenantId = tenantId, GameId = gameId, StreamerId = streamerId, SessionId = sessionId,
                OperationId = operationId
            }, cancellationToken: cancellationToken);
        }
        catch (Exception error) when (error is RpcException or HttpRequestException)
        {
            // Best-effort: the mount refusal proceeds regardless; the next report or the pause
            // timeout bounds the room's future.
            logger.LogDebug(error,
                "The resume-ready report for {StreamerId} was not delivered; the paused room waits.",
                streamerId);
        }
    }
}
