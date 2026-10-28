using Chorus.Redesign.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The routing watch (architecture 4.4) as the edge consumes it: a notice stream that says
/// "the routed world changed", answered by a full snapshot pull (the Routing service's consumer
/// contract). The snapshot's streamer rows name each mount's current room — that is how the
/// gateway learns a queued mount was matched and its round started, without holding any match
/// state of its own. Subscribers are connections that joined a queue; the callback fires once
/// per room change for their streamer — the first apply after subscribing fires whatever the
/// room is, appearance or not, and the connection-side room claim deduplicates re-announcements.
/// A watch that breaks is rebuilt from the last known version.
/// </summary>
public sealed class EdgeRoutingMonitor(Func<GrpcChannel> controlChannelFactory)
{
    private sealed class Subscription(string streamerId, string connectionId,
        Func<string, Task> onRoomChanged)
    {
        public string StreamerId { get; } = streamerId;
        public string ConnectionId { get; } = connectionId;
        public Func<string, Task> OnRoomChanged { get; } = onRoomChanged;
        public string? LastRoom { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Dictionary<string, string> _roomByStreamer = new(StringComparer.Ordinal);

    /// <summary>Subscribes one connection to its streamer's room changes. The callback leaves
    /// the lock before running, so a slow push never blocks the watch loop.</summary>
    public void Subscribe(string streamerId, string connectionId, Func<string, Task> onRoomChanged)
    {
        lock (_gate) _subscriptions.Add(new Subscription(streamerId, connectionId, onRoomChanged));
    }

    public void Unsubscribe(string connectionId)
    {
        lock (_gate) _subscriptions.RemoveAll(subscription => subscription.ConnectionId == connectionId);
    }

    /// <summary>The room a streamer's mount currently points at, or null — the M4-4 fanout
    /// will ask the same question for room endpoints.</summary>
    public string? RoomFor(string streamerId)
    {
        lock (_gate)
            return _roomByStreamer.TryGetValue(streamerId, out var room) && room.Length > 0 ? room : null;
    }

    /// <summary>Applies one snapshot: records the streamer rows and fires a callback for every
    /// subscribed streamer whose room changed. Public so the diff logic is testable without a
    /// control plane.</summary>
    public async Task ApplySnapshotAsync(RoutingSnapshotReply snapshot)
    {
        List<(Subscription Subscription, string Room)>? changed = null;
        lock (_gate)
        {
            foreach (var streamer in snapshot.Streamers)
                _roomByStreamer[streamer.StreamerId] = streamer.RoomId ?? "";
            foreach (var subscription in _subscriptions)
            {
                var room = _roomByStreamer.TryGetValue(subscription.StreamerId, out var value)
                    ? value : "";
                if (string.Equals(subscription.LastRoom, room, StringComparison.Ordinal)) continue;
                subscription.LastRoom = room;
                (changed ??= []).Add((subscription, room));
            }
        }
        if (changed is null) return;
        foreach (var (subscription, room) in changed)
            await subscription.OnRoomChanged(room);
    }

    /// <summary>The watch loop: notices arrive versioned and carry no payload, so every notice
    /// is answered with a full snapshot pull. The host owns this loop's lifetime.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var version = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var channel = controlChannelFactory();
                var routing = new Routing.RoutingClient(channel);
                await foreach (var notice in routing.WatchRouting(
                    new WatchRoutingRequest { KnownVersion = version },
                    cancellationToken: cancellationToken).ResponseStream.ReadAllAsync(cancellationToken))
                {
                    // The routing version is a content hash, not a sequence (control store:
                    // any mutation produces a different version, a missed notice is caught by
                    // difference, not order). "Different" is the change signal — a lower hash
                    // is a newer world just the same.
                    if (notice.Version == version) continue;
                    version = notice.Version;
                    var snapshot = await routing.GetRoutingSnapshotAsync(new Empty(),
                        cancellationToken: cancellationToken);
                    await ApplySnapshotAsync(snapshot);
                }
            }
            catch (RpcException)
            {
                // The stream broke; the next iteration rebuilds from the last known version.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            // A broken watch retries after a breath, not in a hot loop.
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
