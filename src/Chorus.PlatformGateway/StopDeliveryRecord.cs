using System.Threading.Channels;

namespace Chorus.PlatformGateway;

/// <summary>
/// The gateway's process-wide record of the stop-delivery commands it closed at
/// (FIX-END-05/07, architecture 6.4 steps 3-4). The first command for a room installs the
/// closure — the route gate the webhook face consults before any push — and queues the
/// drain pass that disposes pending items terminally and acks the control plane. The gRPC
/// service is created per request, so this record is the process-wide truth the handler,
/// the face and the drain share; a repeated command at the recorded version answers
/// duplicate and queues nothing, and a command naming a different version for a room the
/// gateway already closed is refused, so a late replay can never close a newer round.
/// In-memory by design: the control plane's persisted command and responder rows are the
/// durable facts; this record only makes one gateway's closure, drain and ack idempotent.
/// A restarted gateway forgets it all — the coordinator's re-push carries the same
/// recorded command and the fresh process installs it again.
/// </summary>
public sealed class StopDeliveryRecord
{
    /// <summary>One room's installed closure: the command's own facts plus what this
    /// process observed — the cutoff watermark and the ack state.</summary>
    public sealed class Closure
    {
        public required string TenantId { get; init; }
        public required string GameId { get; init; }
        public required string RoomId { get; init; }
        public required string EndOperationId { get; init; }
        public required long CloseRouteVersion { get; init; }
        public required long RoundGeneration { get; init; }
        /// <summary>Whether this process already landed the ack — later re-drains still
        /// dispose but skip the ack, whose first answer's facts stand.</summary>
        public bool Acked;
    }

    private readonly object _gate = new();
    // Grows with closed rooms for the process's lifetime — the same lifetime as the routes
    // the monitor caches. The control plane's rows are the durable memory, not this map.
    private readonly Dictionary<string, Closure> _closedAt = new();
    // The watermark a room's pushes durably observed, closure or not: a push that landed
    // before the command arrived is still the process's own observation, and the closure
    // inherits it at install — the ack reports what this process saw, never less.
    private readonly Dictionary<string, long> _watermarks = new();
    private readonly HashSet<(string Platform, string AppId)> _observedApps = new();
    private readonly Channel<string> _drainQueue = Channel.CreateUnbounded<string>();

    /// <summary>The queued drain work: room ids whose closure awaits dispose and ack — the
    /// first command arrival and every late ingest into a closed room enqueue.</summary>
    public ChannelReader<string> DrainQueue => _drainQueue.Reader;

    /// <summary>Installs the room's closure from the command's own facts. True when the
    /// room's delivery now stands closed at exactly this version — a first command records
    /// it and queues the drain, a replay at the recorded version confirms it. False when
    /// the room already closed at a different version: nothing is written, and the recorded
    /// version is the one that refused the command.</summary>
    public bool TryClose(string tenantId, string gameId, string roomId, string endOperationId,
        long roundGeneration, long closeRouteVersion, out bool duplicate, out long recordedVersion)
    {
        lock (_gate)
        {
            if (_closedAt.TryGetValue(roomId, out var closed))
            {
                duplicate = true;
                recordedVersion = closed.CloseRouteVersion;
                if (closed.CloseRouteVersion != closeRouteVersion) return false; // stale: refused
                return true;
            }
            _closedAt[roomId] = new Closure
            {
                TenantId = tenantId, GameId = gameId, RoomId = roomId,
                EndOperationId = endOperationId, RoundGeneration = roundGeneration,
                CloseRouteVersion = closeRouteVersion
            };
            duplicate = false;
            recordedVersion = closeRouteVersion;
        }
        // Only the first arrival queues the drain: a duplicate confirms an installed
        // closure, it never re-runs the dispose the first pass already owns.
        _drainQueue.Writer.TryWrite(roomId);
        return true;
    }

    /// <summary>True when the room's delivery stands closed for this generation — the
    /// face's gate before any push. At or below the closed generation refuses, exactly the
    /// comparison the receiver's own fence makes (FIX-END-06): a snapshot carrying an older
    /// generation for the room is stale evidence, and a room id never hosts a newer round,
    /// so nothing legitimate is refused.</summary>
    public bool IsClosed(string roomId, long roundGeneration)
    {
        lock (_gate)
            return _closedAt.TryGetValue(roomId, out var closed)
                && roundGeneration <= closed.RoundGeneration;
    }

    /// <summary>The room's installed closure, or null when no command ever landed here.</summary>
    public Closure? Take(string roomId)
    {
        lock (_gate) return _closedAt.TryGetValue(roomId, out var closed) ? closed : null;
    }

    /// <summary>Records a (platform, app) pair a webhook durably landed under — the
    /// enumeration a drain pass routes, because RouteEvents is platform-and-app scoped and
    /// the gateway knows no other application list.</summary>
    public void ObserveApp(string platform, string appId)
    {
        lock (_gate) _observedApps.Add((platform, appId));
    }

    /// <summary>The pairs observed since process start — empty after a restart, so a fresh
    /// process's first drain pass routes nothing while the pending items stay durably
    /// attributed in the inbox for any later pass or the settlement backstop.</summary>
    public IReadOnlyList<(string Platform, string AppId)> ObservedApps()
    {
        lock (_gate) return _observedApps.ToList();
    }

    /// <summary>Advances the room's cutoff watermark — the highest delivery sequence a
    /// durable push reply carried, recorded whether or not a closure stands yet (the
    /// round's pushes land before the command that closes it). Monotonic: a late reply for
    /// a lower sequence loses.</summary>
    public void RecordCutoff(string roomId, long sequence)
    {
        lock (_gate)
        {
            if (sequence > 0 && (!_watermarks.TryGetValue(roomId, out var seen) || sequence > seen))
                _watermarks[roomId] = sequence;
        }
    }

    /// <summary>The room's observed watermark — 0 when no durable push reply ever carried a
    /// sequence; an honest zero for a process that crashed before observing.</summary>
    public long CutoffOf(string roomId)
    {
        lock (_gate) return _watermarks.TryGetValue(roomId, out var seen) ? seen : 0;
    }

    /// <summary>Marks the room's ack landed. Later re-drains — late events arriving into
    /// the closed room — still dispose but skip the ack: the first answer's facts stand.</summary>
    public void MarkAcked(string roomId)
    {
        lock (_gate)
        {
            if (_closedAt.TryGetValue(roomId, out var closed)) closed.Acked = true;
        }
    }

    /// <summary>Queues one more drain pass for a room whose closure already stands — the
    /// face's leg after a late webhook durably lands in a closed room's inbox. A room
    /// without an installed closure enqueues nothing: there is nothing to dispose as
    /// closed.</summary>
    public void RequestDrain(string roomId)
    {
        lock (_gate)
        {
            if (!_closedAt.ContainsKey(roomId)) return;
        }
        _drainQueue.Writer.TryWrite(roomId);
    }
}
