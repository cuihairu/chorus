using System.Security.Cryptography.X509Certificates;
using Chorus.Rpc;
using Grpc.Net.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// The participation-commit dispatcher (FIX-LINK-04, architecture 7.2 step 6): the control
/// plane delivers each round.started outbox event — the transaction that committed the
/// participation relations, the mounts' round pointers and the Preparing room — to the
/// StreamerServer hosting each participant, so a mount binds its new room without waiting for
/// a poll. The outbox row is the durable record (written in the commit transaction): an event
/// is claimed with a lease, delivered to every resolved participant's owning instance, then
/// retired. A participant whose mount no longer points at the room is moot — the round
/// released it before the dispatch, so stale evidence is never delivered. An unresolved owner
/// or a failed push returns the event to the pending queue for the next pass, and the
/// StreamerServer handler is idempotent, so at-least-once delivery converges. The lost-notice
/// query fallback is FIX-LINK-05.
/// </summary>
public sealed class ParticipationCommitDispatcherService(
    RedesignControlStore controlStore,
    X509Certificate2 clientCertificate,
    X509Certificate2 root,
    X509Certificate2 nodeAuthority,
    string clusterId,
    ILogger<ParticipationCommitDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_PARTICIPATION_DISPATCH_INTERVAL_MS") ?? "1000",
        System.Globalization.CultureInfo.InvariantCulture));

    private static readonly TimeSpan PushDeadline = TimeSpan.FromSeconds(5);

    private static readonly int BatchSize = 32;

    private readonly object _channelLock = new();
    private readonly Dictionary<string, GrpcChannel> _channelsByEndpoint = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var eventEntry in await controlStore.ClaimRoundStartedAsync(BatchSize, stoppingToken))
                    await DriveAsync(eventEntry, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Participation dispatch pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Delivers one claimed event: each payload participant is resolved to its owning
    /// StreamerServer; a resolved owner gets the notice, a moot participant is skipped, an
    /// unresolved one parks the event for the next pass. The whole event retries as one unit —
    /// re-delivery to an already-notified instance is an idempotent no-op on the receiver.</summary>
    private async Task DriveAsync(RoundStartedEvent eventEntry, CancellationToken cancellationToken)
    {
        var targets = await controlStore.ResolveParticipationTargetsAsync(eventEntry.RoomId, cancellationToken);
        var byBinding = targets.ToDictionary(target => target.BindingId, StringComparer.Ordinal);
        // Participants grouped by owning instance, so one channel carries a room's whole set.
        var byInstance = new Dictionary<string, List<NotifyCommittedRequest>>(StringComparer.Ordinal);
        var unresolved = false;
        foreach (var participant in eventEntry.Participants)
        {
            if (!byBinding.TryGetValue(participant.BindingId, out var target))
                continue; // Moot: the mount's participation is gone or its pointer moved away.
            // The dispatch-time pointer is the authority; a payload that disagrees (the binding
            // advanced past this commit, or the row is foreign) is moot, never delivered.
            if (target.RoundGeneration != participant.RoundGeneration)
                continue;
            if (string.IsNullOrEmpty(target.OwnerInstanceId) || string.IsNullOrEmpty(target.Endpoint))
            {
                unresolved = true; // The owning instance is unknown: retry, never guess an address.
                continue;
            }
            if (!byInstance.TryGetValue(target.Endpoint, out var set))
                byInstance[target.Endpoint] = set = [];
            set.Add(new NotifyCommittedRequest
            {
                TenantId = eventEntry.TenantId, GameId = eventEntry.GameId, RoomId = eventEntry.RoomId,
                RoundGeneration = participant.RoundGeneration, BindingId = target.BindingId,
                BindingGeneration = target.BindingGeneration, StreamerId = target.StreamerId
            });
        }
        if (!unresolved)
        {
            foreach (var (endpoint, requests) in byInstance)
            {
                try
                {
                    await NotifyAsync(endpoint, requests, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // The instance is unreachable: park the event; the next pass retries, and
                    // the idempotent receiver makes a repeat a no-op.
                    logger.LogWarning(error,
                        "Participation commit notice for {Room} to {Endpoint} failed; the event is retried.",
                        eventEntry.RoomId, endpoint);
                    unresolved = true;
                    break;
                }
            }
        }
        await controlStore.CompleteRoundStartedAsync(eventEntry.EffectId, delivered: !unresolved,
            cancellationToken);
    }

    private async Task NotifyAsync(string endpoint, IReadOnlyList<NotifyCommittedRequest> requests,
        CancellationToken cancellationToken)
    {
        var client = new Participation.ParticipationClient(ChannelFor(endpoint));
        foreach (var request in requests)
            await client.NotifyCommittedAsync(request,
                deadline: DateTime.UtcNow.Add(PushDeadline), cancellationToken: cancellationToken);
    }

    private GrpcChannel ChannelFor(string endpoint)
    {
        lock (_channelLock)
        {
            if (_channelsByEndpoint.TryGetValue(endpoint, out var channel)) return channel;
            return _channelsByEndpoint[endpoint] = ProcessGrpcClientFactory.Create(
                new Uri(endpoint), clientCertificate, root, nodeAuthority, clusterId, "streamer-server");
        }
    }
}
