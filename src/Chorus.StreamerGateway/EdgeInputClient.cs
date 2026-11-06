using Chorus.Rpc;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerGateway;

/// <summary>
/// The RoundInput boundary as the edge sees it (contracts 3.1): a streamer command enters the
/// round's inbox durably, and a LeaveRound withdraws the mount's participation while the room
/// runs on. The gateway names the tenant on every call (its certificate is cluster-scoped);
/// the receipt means durably accepted — the applied word arrives on the sync stream, never
/// from this boundary.
/// </summary>
public sealed class EdgeInputClient(Func<GrpcChannel> channelFactory)
{
    private readonly Lazy<GrpcChannel> _channel = new(channelFactory);

    public async Task<ParticipationReply> WithdrawParticipationAsync(string tenantId, string sessionId,
        string roomId, string gameId, string operationId, CancellationToken cancellationToken)
        => await new RoundInput.RoundInputClient(_channel.Value).WithdrawParticipationAsync(
            new WithdrawParticipationRequest
            {
                TenantId = tenantId, SessionId = sessionId, RoomId = roomId, GameId = gameId,
                OperationId = operationId
            }, cancellationToken: cancellationToken);
}
