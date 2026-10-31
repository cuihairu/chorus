using Chorus.Rpc;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.PlatformGateway;

/// <summary>
/// The routing facts the webhook face needs from the directory: resolve a callback's live key
/// to its room's push target, and refresh the directory on demand (FIX-INPUT-05) — a refusal
/// that names a stale owner picture is answered with one direct snapshot pull, not by waiting
/// for the watch's next notice.
/// </summary>
public interface IRoomRoutingDirectory
{
    /// <summary>The room a platform callback's live key currently belongs to, or false when the
    /// callback has no valid attribution — no live mount, no room, or an owner that serves no
    /// endpoint yet.</summary>
    bool TryResolve(string platform, string canonicalLiveKey, out GatewayRoutingMonitor.RoomRouteTarget target);

    /// <summary>One direct snapshot pull, applied to the cache. False when the control plane
    /// could not be reached — the caller keeps its previous picture.</summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The routing watch (architecture 4.4) as the webhook face consumes it: a notice stream that
/// says "the routed world changed", answered by a full snapshot pull — the same contract the
/// streamer gateway's edge monitor keeps. The cache resolves a platform callback's canonical
/// live session key to the room it currently belongs to, and that room's owner instance, epoch
/// and serving endpoint: the facts the face needs to push the event straight at the owning
/// game host (FIX-INPUT-02) instead of leaving it for a later pull. The gateway holds no
/// mount or room state of its own — a broken watch or a lost cache costs at most one stale
/// push that the ownership fence refuses, and the rebuild from the last known version makes
/// the resolve exact again.
/// </summary>
public sealed class GatewayRoutingMonitor(Func<GrpcChannel> controlChannelFactory) : IRoomRoutingDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Platform, string Key), StreamerFacts> _streamsByLiveKey =
        new();
    private readonly Dictionary<string, RoomFacts> _roomsById = new(StringComparer.Ordinal);

    /// <summary>Where a live streamer binding points: its binding id and the room it carries.</summary>
    public sealed record StreamerFacts(string BindingId, string RoomId);

    /// <summary>The routing facts a room's push needs: the tenant that owns it, the game it
    /// belongs to, the owner instance and epoch, and where that owner serves.</summary>
    public sealed record RoomFacts(string TenantId, string GameId, string OwnerInstanceId,
        long Epoch, string? Endpoint);

    /// <summary>Everything the face needs to push one event into its room.</summary>
    public sealed record RoomRouteTarget(string RoomId, string TenantId, string GameId,
        string BindingId, string OwnerInstanceId, long Epoch, string Endpoint);

    /// <summary>The room a platform callback's live key currently belongs to, or false — the
    /// callback has no valid attribution (the mount is not live, not in a room, or the owner
    /// has not reported an endpoint yet), which the face answers with a durable wait rather
    /// than a guess.</summary>
    public bool TryResolve(string platform, string canonicalLiveKey, out RoomRouteTarget target)
    {
        lock (_gate)
        {
            target = null!;
            if (!_streamsByLiveKey.TryGetValue((platform, canonicalLiveKey), out var stream))
                return false;
            if (!_roomsById.TryGetValue(stream.RoomId, out var room) || room.Endpoint is not { Length: > 0 })
                return false;
            target = new RoomRouteTarget(stream.RoomId, room.TenantId, room.GameId,
                stream.BindingId, room.OwnerInstanceId, room.Epoch, room.Endpoint);
            return true;
        }
    }

    /// <summary>Applies one snapshot. Public so the diff logic is testable without a control
    /// plane.</summary>
    public void ApplySnapshot(RoutingSnapshotReply snapshot)
    {
        lock (_gate)
        {
            foreach (var room in snapshot.Rooms)
                _roomsById[room.RoomId] = new RoomFacts(room.TenantId, room.GameId,
                    room.OwnerInstanceId, room.Epoch, room.Endpoint.Length > 0 ? room.Endpoint : null);
            foreach (var streamer in snapshot.Streamers)
                if (streamer.CanonicalLiveSessionKey.Length > 0)
                    _streamsByLiveKey[(streamer.Platform, streamer.CanonicalLiveSessionKey)] =
                        new StreamerFacts(streamer.BindingId, streamer.RoomId);
        }
    }

    /// <summary>One direct snapshot pull applied to the cache (FIX-INPUT-05): a push that a
    /// stale owner picture refused must not wait for the watch's next notice — the face asks
    /// for the directory itself, and a pull that cannot reach the control plane leaves the
    /// previous picture in place rather than an empty one.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var channel = controlChannelFactory();
            var routing = new Routing.RoutingClient(channel);
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty(),
                deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
            ApplySnapshot(snapshot);
            return true;
        }
        catch (RpcException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
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
                    // The routing version is a content hash, not a sequence: any mutation
                    // produces a different version, and "different" is the change signal — a
                    // lower hash is a newer world just the same.
                    if (notice.Version == version) continue;
                    version = notice.Version;
                    var snapshot = await routing.GetRoutingSnapshotAsync(new Empty(),
                        cancellationToken: cancellationToken);
                    ApplySnapshot(snapshot);
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
