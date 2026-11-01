using Chorus.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.PlatformGateway;

/// <summary>
/// The step-4 worker (architecture 6.4, FIX-END-07): one drain pass per closed room, then
/// the DeliveryStopped report. The pass routes the (platform, app) pairs this process
/// observed — RouteEvents is the only production-reachable path to a terminal disposition,
/// and under the receiver's generation fence (FIX-END-06) every closed-room item it touches
/// lands OutOfRound with the room named — then acks with the closure's own facts: the exact
/// version, the cutoff watermark the push leg observed, and the informational drain counts.
/// The ack is idempotent and first-answer-wins server-side, so the retry-until-accepted
/// loop and a restarted process's re-ack are both safe. A crash mid-pass changes nothing
/// durable: the responder stays Pending, the coordinator re-pushes the recorded command,
/// and the fresh process installs the closure and drains again — each pass uses a fresh
/// operation id and the store's routing window cannot disposition an event twice. Events
/// arriving after a pass stay durably attributed in the inbox; the face's ingest leg asks
/// for one more pass, and any later drain or the settlement backstop completes them.
/// </summary>
public sealed class DeliveryStopDrainService(
    StopDeliveryRecord stopDeliveries,
    Func<GrpcChannel> dataChannelFactory,
    Func<GrpcChannel> controlChannelFactory,
    ILogger<DeliveryStopDrainService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AckRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long one room's ack rides out a control plane it cannot reach before
    /// the room re-queues and the pass moves on — liveness for the queue, never a loss of
    /// work: the re-queued pass resumes with a fresh operation id and an idempotent ack.</summary>
    private static readonly TimeSpan AckRetryWindow = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var dataChannel = dataChannelFactory();
        using var controlChannel = controlChannelFactory();
        var input = new RoundInput.RoundInputClient(dataChannel);
        var roundEnd = new RoundEnd.RoundEndClient(controlChannel);
        await foreach (var roomId in stopDeliveries.DrainQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (!await DrainAndAckAsync(roomId, input, roundEnd, stoppingToken))
                    stopDeliveries.RequestDrain(roomId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (RpcException error) when (error.StatusCode is StatusCode.FailedPrecondition
                or StatusCode.InvalidArgument or StatusCode.PermissionDenied
                or StatusCode.Unauthenticated or StatusCode.NotFound)
            {
                // A definitive refusal — a version the command row does not hold, an
                // instance outside the frozen responder set — no retry can fix: the
                // responder stays Pending, the evidence is this log, and the operator is
                // the escalation (FIX-END-08 hardens the barrier's validation).
                logger?.LogError(
                    "Delivery-stop drain for {RoomId} was refused: {Status} {Detail}",
                    roomId, error.StatusCode, error.Status.Detail);
            }
            catch (Exception error)
            {
                logger?.LogWarning("Delivery-stop drain for {RoomId} failed: {Message}",
                    roomId, error.Message);
                stopDeliveries.RequestDrain(roomId);
            }
        }
    }

    /// <summary>The one-room pass: dispose what this process can reach, then report.
    /// Returns false when the ack could not land inside its window — the caller re-queues
    /// the room; everything durable the pass achieved stands.</summary>
    internal async Task<bool> DrainAndAckAsync(string roomId, RoundInput.RoundInputClient input,
        RoundEnd.RoundEndClient roundEnd, CancellationToken cancellationToken)
    {
        var closure = stopDeliveries.Take(roomId);
        if (closure is null) return true;
        var drainedOutOfRound = 0L;
        var drainedRouted = 0L;
        foreach (var (platform, appId) in stopDeliveries.ObservedApps())
        {
            // A fresh operation id per call: a repeated id would replay the recorded reply
            // of the earlier pass and count its events a second time.
            var request = new RouteEventsRequest
            {
                Platform = platform, AppId = appId, MaxEvents = 64,
                OperationId = $"drain:{roomId}:{Guid.NewGuid():N}"
            };
            // RouteEvents is (platform, app)-scoped across every room: the pass routes
            // other rooms' pendings as a side effect (they reach their own rooms) and the
            // count below keeps only this room's items.
            RouteEventsReply routed;
            try
            {
                routed = await input.RouteEventsAsync(request,
                    deadline: DateTime.UtcNow.Add(RpcTimeout),
                    cancellationToken: cancellationToken);
            }
            catch (RpcException error) when (error.StatusCode is StatusCode.DeadlineExceeded
                or StatusCode.Unavailable or StatusCode.Internal or StatusCode.Unknown)
            {
                // Transient: this pair's pendings stay durably attributed — a later pass
                // (the re-queue below, a late arrival's request) picks them up.
                logger?.LogWarning("Delivery-stop drain routing {Platform}/{App} for {RoomId} " +
                    "could not land: {Message}", platform, appId, roomId, error.Message);
                continue;
            }
            foreach (var item in routed.Events)
            {
                if (item.RoomId != roomId) continue;
                if (item.Disposition == "OutOfRound") drainedOutOfRound++;
                else if (item.Disposition == "Routed") drainedRouted++;
            }
        }
        // A re-drain for late arrivals: the dispose stands, the first ack's facts stand too.
        if (closure.Acked) return true;

        var giveUpAt = DateTime.UtcNow.Add(AckRetryWindow);
        while (true)
        {
            try
            {
                await roundEnd.AckDeliveryStoppedAsync(new AckDeliveryStoppedRequest
                {
                    TenantId = closure.TenantId, GameId = closure.GameId, RoomId = closure.RoomId,
                    EndOperationId = closure.EndOperationId,
                    CloseRouteVersion = closure.CloseRouteVersion,
                    CutoffSequence = stopDeliveries.CutoffOf(roomId),
                    DrainedOutOfRound = drainedOutOfRound, DrainedRouted = drainedRouted
                }, deadline: DateTime.UtcNow.Add(RpcTimeout),
                    cancellationToken: cancellationToken);
                stopDeliveries.MarkAcked(roomId);
                logger?.LogInformation(
                    "delivery-stopped acked room={RoomId} version={Version} cutoff={Cutoff} " +
                    "outOfRound={OutOfRound} routed={Routed}",
                    closure.RoomId, closure.CloseRouteVersion, stopDeliveries.CutoffOf(roomId),
                    drainedOutOfRound, drainedRouted);
                return true;
            }
            catch (RpcException error) when (error.StatusCode is StatusCode.DeadlineExceeded
                or StatusCode.Unavailable or StatusCode.Internal or StatusCode.Unknown)
            {
                if (DateTime.UtcNow >= giveUpAt) return false;
                await Task.Delay(AckRetryDelay, cancellationToken);
            }
        }
    }
}
