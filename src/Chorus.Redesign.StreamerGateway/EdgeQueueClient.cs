using Chorus.Redesign.Rpc;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The MatchQueue boundary as the edge sees it (contracts 2, Match/ControlServer): the mount's
/// ticket is the unit — enqueue, cancel and read, all by binding. The gateway names the tenant
/// on every call (it is cluster-scoped); the queue store still fences the binding's tenant.
/// Ticket states are the store's vocabulary (Queued/Reserved/Preparing/Committed/Cancelled/
/// Expired); the edge's client-facing word for Committed is "Matched".
/// </summary>
public sealed class EdgeQueueClient(Func<GrpcChannel> channelFactory)
{
    private readonly Lazy<GrpcChannel> _channel = new(channelFactory);

    public async Task<TicketReply> EnqueueAsync(string tenantId, string gameId, string bindingId,
        string sessionId, string partitionKey, string operationId, CancellationToken cancellationToken)
        => await new MatchQueue.MatchQueueClient(_channel.Value).EnqueueTicketAsync(
            new EnqueueTicketRequest
            {
                TenantId = tenantId, GameId = gameId, BindingId = bindingId, SessionId = sessionId,
                PartitionKey = partitionKey, OperationId = operationId
            }, cancellationToken: cancellationToken);

    public async Task<TicketReply> CancelAsync(string tenantId, string gameId, string bindingId,
        string operationId, CancellationToken cancellationToken)
        => await new MatchQueue.MatchQueueClient(_channel.Value).CancelTicketAsync(
            new CancelTicketRequest
            {
                TenantId = tenantId, GameId = gameId, BindingId = bindingId, OperationId = operationId
            }, cancellationToken: cancellationToken);

    /// <summary>The mount's current ticket, or null when there is none on record.</summary>
    public async Task<TicketProto?> GetTicketAsync(string tenantId, string gameId, string bindingId,
        CancellationToken cancellationToken)
    {
        var reply = await new MatchQueue.MatchQueueClient(_channel.Value).GetTicketAsync(
            new GetTicketRequest { TenantId = tenantId, GameId = gameId, BindingId = bindingId },
            cancellationToken: cancellationToken);
        return reply.Found ? reply.Ticket : null;
    }
}

/// <summary>
/// The match partition an edge client lands in (contracts 2: candidates only meet inside one
/// partition). The gateway composes it from the facts it can name — the tenant, the game, the
/// rules version the client declared at startGameplay, and the queue type. Anything the client
/// hid in the join options does not widen the partition: two mounts that never agreed on these
/// four facts must never share a round window.
/// </summary>
public static class EdgeQueueKeys
{
    public static string PartitionFor(string tenantId, string gameId, string? rulesVersion, string queueType)
        => $"{tenantId}/{gameId}/{(rulesVersion is { Length: > 0 } ? rulesVersion : "-")}/{queueType}";
}

/// <summary>The ticket vocabulary the client sees (contracts 3.2 MatchStatus.state): the store's
/// states pass through untouched, except Committed — the round exists — which the protocol
/// spells "Matched".</summary>
public static class EdgeMatchStates
{
    public const string Matched = "Matched";
    public const string Queued = "Queued";
    public const string Cancelled = "Cancelled";

    public static string FromStore(string storeState) =>
        storeState == "Committed" ? Matched : storeState;
}
