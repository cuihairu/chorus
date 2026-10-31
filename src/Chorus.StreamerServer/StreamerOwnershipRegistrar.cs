using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerServer;

/// <summary>
/// Claims streamer-entity ownership on the control plane (architecture 10.3): the instance that
/// granted a session becomes the entity's owner-of-record, so routing can name it and the
/// ownership registrar keeps the lease alive while this process reports live. The claim names
/// the acting session (FIX-AUTH-02) and is admitted only while that session is the streamer's
/// current one — a displaced holder's late claim cannot flip routing back. A claim failure
/// never fails the session path — the session domain is the authority for sessions; ownership
/// is the routing picture, and a host cut off from control keeps serving its sessions.
/// </summary>
public sealed class StreamerOwnershipRegistrar(
    Func<GrpcChannel> channelFactory, string ownerInstanceId, int leaseSeconds = 15)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    public string OwnerInstanceId => ownerInstanceId;

    public async Task ClaimAsync(string tenantId, string gameId, string streamerId,
        string sessionId, long sessionVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var channel = channelFactory();
            var control = new ControlService.ControlServiceClient(channel);
            await control.ClaimOwnershipAsync(new OwnershipRequest
            {
                TenantId = tenantId, GameId = gameId, Kind = "streamer", EntityId = streamerId,
                OwnerInstanceId = ownerInstanceId, LeaseSeconds = leaseSeconds,
                SessionId = sessionId, SessionVersion = sessionVersion
            }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken);
        }
        catch (Exception error) when (error is RpcException or OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"streamer ownership claim for {streamerId} on {ownerInstanceId} failed: {error.Message}");
        }
    }
}
