using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerServer;

/// <summary>
/// Submits a mount's match intent through the control plane (FIX-LINK-03, architecture 6.4):
/// the business path submits to the control plane, which verifies the registered route source
/// and forwards the ticket to the queue the coordinator scans. A failure never fails the mount
/// — the data plane owns the binding, the intent is the business picture, and the next mount
/// (or a recovery pass, FIX-LINK-05) submits again.
/// </summary>
public sealed class StreamerMatchIntentSubmitter(Func<GrpcChannel> channelFactory)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    public async Task SubmitAsync(string tenantId, string gameId, string streamerId,
        string bindingId, long bindingGeneration, string sessionId, string partitionKey,
        string operationId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var channel = channelFactory();
            var reply = await new MatchIntent.MatchIntentClient(channel).SubmitMatchIntentAsync(
                new SubmitMatchIntentRequest
                {
                    TenantId = tenantId, GameId = gameId, BindingId = bindingId,
                    BindingGeneration = bindingGeneration, SessionId = sessionId,
                    PartitionKey = partitionKey, OperationId = operationId
                }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken);
            Console.WriteLine(
                $"match intent for {streamerId} binding {bindingId} generation {bindingGeneration} " +
                $"queued in partition {partitionKey} as ticket {reply.Ticket?.TicketId ?? "none"}");
        }
        catch (Exception error) when (error is RpcException or OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"match intent for {streamerId} binding {bindingId} generation {bindingGeneration} " +
                $"failed: {error.Message}");
        }
    }
}
