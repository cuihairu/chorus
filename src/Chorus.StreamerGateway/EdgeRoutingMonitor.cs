using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerGateway;

/// <summary>
/// The routing watch (architecture 4.4) as the edge consumes it: a notice stream that says
/// "the routed world changed", answered by a full snapshot pull (the Routing service's consumer
/// contract). The snapshot's streamer rows name each mount's current room — that is how the
/// gateway learns a queued mount was matched and its round started, without holding any match
/// state of its own. Subscribers are connections that joined a queue; the callback fires once
/// per room change for their streamer — the first apply after subscribing fires whatever the
/// room is, appearance or not, and the connection-side room claim deduplicates re-announcements.
/// A watch that breaks is rebuilt from the last known version.
///
/// FIX-LINK-08: the picture is installed only once the control plane has acknowledged the exact
/// version this monitor answers resolves from (architecture 6.4: "缺失或未确认路由不能猜测玩家所在
/// 房间"), and a mount resolves a room only when the streamer row's binding id and generation are
/// its own — a snapshot row for the streamer whose mount this connection is not is never read as
/// placement (room-recovery.md: the gateway does not infer room placement).
/// </summary>
public sealed class EdgeRoutingMonitor(
    Func<GrpcChannel> controlChannelFactory,
    Func<long, CancellationToken, Task<bool>>? confirmVersion = null)
{
    private static readonly TimeSpan ConfirmDeadline = TimeSpan.FromSeconds(5);

    private sealed class Subscription(string streamerId, string connectionId,
        Func<string, Task> onRoomChanged)
    {
        public string StreamerId { get; } = streamerId;
        public string ConnectionId { get; } = connectionId;
        public Func<string, Task> OnRoomChanged { get; } = onRoomChanged;
        public string? LastRoom { get; set; }
    }

    /// <summary>The streamer row the gateway must fence a mount against: which binding the row
    /// describes, at which generation, and which round it currently sits in.</summary>
    private sealed record StreamerFacts(string BindingId, string RoomId, long BindingGeneration,
        long RoundGeneration, string State, string TenantId);

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Dictionary<string, StreamerFacts> _streamers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomFacts> _roomsById = new(StringComparer.Ordinal);
    private long _appliedVersion;
    private long _confirmedVersion;

    /// <summary>The routing facts a room's fan-out needs: the tenant that owns it, where the
    /// owning game host serves, at which epoch, in which status.</summary>
    public sealed record RoomFacts(string TenantId, string GameId, string Endpoint, long Epoch,
        string Status, string OwnerInstanceId);

    /// <summary>A room resolved for one mount, fenced to the mount's own binding facts.</summary>
    public sealed record FencedRoom(string RoomId, RoomFacts Facts, long BindingGeneration, long RoundGeneration);

    /// <summary>The versions this monitor applied and last had acknowledged; exposed for tests
    /// and diagnostics — the gate is open only when they are equal and non-zero.</summary>
    public (long Applied, long Confirmed) Versions
    {
        get { lock (_gate) return (_appliedVersion, _confirmedVersion); }
    }

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
    /// will ask the same question for room endpoints. Ungated on purpose: the product paths
    /// resolve rooms through <see cref="ResolveMount"/>; this stays for diagnostics and the
    /// watch-diff tests.</summary>
    public string? RoomFor(string streamerId)
    {
        lock (_gate)
            return _streamers.TryGetValue(streamerId, out var facts) && facts.RoomId.Length > 0
                ? facts.RoomId : null;
    }

    /// <summary>The routing facts of one room, or null when routing does not know it.</summary>
    public RoomFacts? RoomRouteFor(string roomId)
    {
        lock (_gate) return _roomsById.TryGetValue(roomId, out var facts) ? facts : null;
    }

    /// <summary>The mount's tenant from its routing row, or null when routing does not know the
    /// streamer. The tenant is a mount fact, not a placement inference — it names the tenant
    /// the gateway must ask the StreamerServer with on a resume, which names no tenant itself.</summary>
    public string? TenantFor(string streamerId)
    {
        lock (_gate)
            return _streamers.TryGetValue(streamerId, out var facts) && facts.TenantId.Length > 0
                ? facts.TenantId : null;
    }

    /// <summary>
    /// Resolves the room a mount participates in — only from a confirmed picture whose streamer
    /// row is the mount's own binding at its own generation, and only when routing knows the
    /// room's endpoint. Anything less answers null: the caller must not guess a placement
    /// (architecture 6.4). A null means "not yet known", not "no room".
    /// </summary>
    public FencedRoom? ResolveMount(string streamerId, string bindingId, long mountGeneration)
    {
        if (bindingId.Length == 0 || mountGeneration <= 0) return null;
        lock (_gate)
        {
            if (_confirmedVersion != _appliedVersion || _appliedVersion == 0) return null;
            if (!_streamers.TryGetValue(streamerId, out var facts)) return null;
            if (!StringComparer.Ordinal.Equals(facts.BindingId, bindingId) ||
                facts.BindingGeneration != mountGeneration) return null;
            return Fence(facts.RoomId, facts.BindingGeneration, facts.RoundGeneration);
        }
    }

    /// <summary>Resolves one room for a mount: the named room must be the one the mount's own
    /// streamer row claims, at the same binding and generations, from a confirmed picture.</summary>
    public FencedRoom? ResolveRoom(string roomId, string bindingId, long bindingGeneration,
        long roundGeneration)
    {
        if (roomId.Length == 0 || bindingId.Length == 0) return null;
        lock (_gate)
        {
            if (_confirmedVersion != _appliedVersion || _appliedVersion == 0) return null;
            foreach (var facts in _streamers.Values)
            {
                if (!StringComparer.Ordinal.Equals(facts.BindingId, bindingId) ||
                    !StringComparer.Ordinal.Equals(facts.RoomId, roomId) ||
                    facts.BindingGeneration != bindingGeneration ||
                    facts.RoundGeneration != roundGeneration) continue;
                return Fence(roomId, facts.BindingGeneration, facts.RoundGeneration);
            }
            return null;
        }
    }

    private FencedRoom? Fence(string roomId, long bindingGeneration, long roundGeneration)
    {
        if (roomId.Length == 0) return null;
        if (!_roomsById.TryGetValue(roomId, out var room) || room.Endpoint.Length == 0) return null;
        return new FencedRoom(roomId, room, bindingGeneration, roundGeneration);
    }

    /// <summary>Applies one snapshot: records the streamer rows and the version. No subscriber
    /// is notified here — a picture the control plane has not acknowledged must never produce a
    /// push, so the notify leg belongs to <see cref="ConfirmAppliedAsync"/>. Public so the
    /// apply logic is testable without a control plane.</summary>
    public void ApplySnapshotAsync(RoutingSnapshotReply snapshot)
    {
        lock (_gate)
        {
            foreach (var room in snapshot.Rooms)
                _roomsById[room.RoomId] = new RoomFacts(room.TenantId, room.GameId,
                    room.Endpoint ?? "", room.Epoch, room.Status, room.OwnerInstanceId);
            foreach (var streamer in snapshot.Streamers)
                _streamers[streamer.StreamerId] = new StreamerFacts(streamer.BindingId,
                    streamer.RoomId ?? "", streamer.BindingGeneration, streamer.RoundGeneration,
                    streamer.State, streamer.TenantId);
            _appliedVersion = snapshot.Version;
        }
    }

    /// <summary>
    /// Asks the control plane to acknowledge the version this monitor currently answers
    /// resolves from. On success the gate opens for that exact version and any change the
    /// applied picture carried is notified; on failure the gate stays closed and the next
    /// notice or pull retries. A monitor without a wired confirm never opens its gate.
    /// </summary>
    public async Task ConfirmAppliedAsync(CancellationToken cancellationToken)
    {
        long applied;
        lock (_gate) applied = _appliedVersion;
        if (applied == 0 || confirmVersion is null) return;
        bool confirmed;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ConfirmDeadline);
            confirmed = await confirmVersion(applied, deadline.Token);
        }
        catch (Exception error) when (error is RpcException or OperationCanceledException)
        {
            // A plane round-trip that fails leaves the gate as it was: closed for the new
            // version, so no push is attempted from a picture the control plane has not seen.
            return;
        }
        if (!confirmed) return;
        List<(Subscription Subscription, string Room)>? changed = null;
        lock (_gate)
        {
            if (_appliedVersion != applied) return;
            _confirmedVersion = applied;
            changed = CollectChanges();
        }
        await NotifyAsync(changed);
    }

    private List<(Subscription Subscription, string Room)>? CollectChanges()
    {
        List<(Subscription Subscription, string Room)>? changed = null;
        foreach (var subscription in _subscriptions)
        {
            var room = _streamers.TryGetValue(subscription.StreamerId, out var facts)
                ? facts.RoomId : "";
            if (string.Equals(subscription.LastRoom, room, StringComparison.Ordinal)) continue;
            subscription.LastRoom = room;
            (changed ??= []).Add((subscription, room));
        }
        return changed;
    }

    private static async Task NotifyAsync(List<(Subscription Subscription, string Room)>? changed)
    {
        if (changed is null) return;
        foreach (var (subscription, room) in changed)
            await subscription.OnRoomChanged(room);
    }

    /// <summary>Pulls the snapshot now, applies it and asks the control plane to confirm it. The
    /// watch beat is a delivery optimization, not a freshness bound (architecture 4.4): a
    /// consumer that needs a fact the cached world lacks — a room that just became deliverable
    /// (FIX-END-03: routes publish only once the actor served, which is after the mount already
    /// points at the room) — re-pulls instead of waiting out the next notice.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var channel = controlChannelFactory();
        var routing = new Routing.RoutingClient(channel);
        var snapshot = await routing.GetRoutingSnapshotAsync(new Empty(),
            cancellationToken: cancellationToken);
        ApplySnapshotAsync(snapshot);
        await ConfirmAppliedAsync(cancellationToken);
    }

    /// <summary>The watch loop: notices arrive versioned and carry no payload, so every notice
    /// is answered with a full snapshot pull and a confirm. The host owns this loop's lifetime.</summary>
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
                    ApplySnapshotAsync(snapshot);
                    await ConfirmAppliedAsync(cancellationToken);
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
