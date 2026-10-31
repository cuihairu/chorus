using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.GameServer;

/// <summary>
/// Step 1's reliable report (architecture 6.4, FIX-END-04): the game host tells the control
/// plane that the final frame and the frozen result committed and that the actor stopped its
/// battle ticks. The business id is derived from the room — the same id a recovering host and
/// the control plane's discovery sweep produce — so any later reporter replays the one
/// operation instead of creating a second: a replay answers Duplicate and never re-awards
/// (settlement is idempotent per grant). The in-process retry is bounded; durability is the
/// deterministic id plus the discovery net, not this process living forever.
/// </summary>
public sealed class RoomEndReporter(Func<GrpcChannel> controlChannelFactory, string tenantId)
{
    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)
    ];

    /// <summary>Reports the frozen result. True when the control plane acknowledged (a
    /// Duplicate replay counts — the operation already exists under this business id).</summary>
    public async Task<bool> ReportFinalAsync(string gameId, string roomId, long finalTick,
        long resultVersion, CancellationToken cancellationToken = default)
    {
        var endOperationId = roomId + "/end";
        for (var attempt = 0; attempt < RetryBackoff.Length; attempt++)
        {
            try
            {
                using var channel = controlChannelFactory();
                var client = new RoundEnd.RoundEndClient(channel);
                var reply = await client.BeginEndAsync(new BeginEndRequest
                {
                    TenantId = tenantId, GameId = gameId, RoomId = roomId,
                    EndOperationId = endOperationId, FinalTick = finalTick,
                    ResultVersion = resultVersion
                }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
                return reply.EndOperationId.Length > 0;
            }
            catch (RpcException)
            {
                // The plane is briefly unreachable or refused; the next attempt retries.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                // The attempt's own deadline elapsed; the next attempt retries.
            }
            if (attempt < RetryBackoff.Length - 1)
            {
                try { await Task.Delay(RetryBackoff[attempt], cancellationToken); }
                catch (OperationCanceledException) { return false; }
            }
        }
        return false;
    }
}
