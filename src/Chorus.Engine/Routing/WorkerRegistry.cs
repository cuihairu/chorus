namespace Chorus.Engine.Routing;

/// <summary>One worker's registration and last-seen stamp.</summary>
/// <param name="WorkerId">Stable worker id (e.g. EngineHost instance id).</param>
/// <param name="Roles">Optional role tag (Gateway:Roles-style; informational).</param>
/// <param name="LastHeartbeatMs">Monotonic max of reported heartbeats (cluster-wide clock domain).</param>
public sealed record WorkerStatus(string WorkerId, string? Roles, long LastHeartbeatMs);

/// <summary>
/// Worker registry and health tracking (todo §14 control-plane item, contract =
/// 总体架构审计 §5.2 Worker 分配): the registry that will publish the
/// worker set consumed by <see cref="RoomAffinity.Assign"/> and the whole
/// <see cref="RoomRouteTable"/> push (§5.3) when stage-3 multi-worker wiring
/// lands (today's single worker is the degenerate case). Health is
/// observational: a worker whose last heartbeat is older than the window is
/// excluded from <see cref="Healthy"/> but stays registered — eviction is an
/// explicit control decision (<see cref="Remove"/>), never a side effect of
/// staleness. Heartbeats are monotonic max, so late out-of-order reports never
/// move last-seen backward. In-memory, process-local: persistence/leases land
/// with §15, wiring lands with stage-3 gating (runtime CPU/room-count data).
/// </summary>
public sealed class WorkerRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string? Roles, long LastHeartbeatMs)> _workers = new(StringComparer.Ordinal);
    private readonly int _staleAfterMs;

    /// <param name="staleAfterMs">Health window; a worker healthy if last heartbeat within this many ms.</param>
    public WorkerRegistry(int staleAfterMs = 30_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(staleAfterMs);
        _staleAfterMs = staleAfterMs;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _workers.Count;
            }
        }
    }

    /// <summary>
    /// Register (or re-register) a worker: idempotent upsert so a restarted
    /// worker re-joins with a fresh heartbeat; roles are overwritten.
    /// </summary>
    public void Register(string workerId, string? roles = null, long nowMs = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        lock (_gate)
        {
            _workers[workerId] = (roles, nowMs);
        }
    }

    /// <summary>Graceful shutdown; only removes workers that exist (visible to the caller).</summary>
    public bool Remove(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        lock (_gate)
        {
            return _workers.Remove(workerId);
        }
    }

    /// <summary>Record a heartbeat; fails visibly for unregistered workers. Never moves last-seen backward.</summary>
    public bool Heartbeat(string workerId, long nowMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        lock (_gate)
        {
            if (!_workers.TryGetValue(workerId, out var current))
            {
                return false;
            }

            if (nowMs > current.LastHeartbeatMs)
            {
                _workers[workerId] = (current.Roles, nowMs);
            }

            return true;
        }
    }

    /// <summary>Whether the worker is registered and its last heartbeat is inside the health window.</summary>
    public bool IsHealthy(string workerId, long nowMs)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(workerId, out var current)
                && nowMs - current.LastHeartbeatMs < _staleAfterMs;
        }
    }

    /// <summary>Registered workers whose last heartbeat is inside the health window, sorted by id.</summary>
    public IReadOnlyList<WorkerStatus> Healthy(long nowMs)
    {
        lock (_gate)
        {
            return _workers
                .Where(pair => nowMs - pair.Value.LastHeartbeatMs < _staleAfterMs)
                .Select(pair => new WorkerStatus(pair.Key, pair.Value.Roles, pair.Value.LastHeartbeatMs))
                .OrderBy(status => status.WorkerId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>All registered workers (healthy or stale), sorted by id.</summary>
    public IReadOnlyList<WorkerStatus> Snapshot()
    {
        lock (_gate)
        {
            return _workers
                .Select(pair => new WorkerStatus(pair.Key, pair.Value.Roles, pair.Value.LastHeartbeatMs))
                .OrderBy(status => status.WorkerId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>Registered worker ids sorted Ordinal — the set shape <see cref="RoomAffinity.Assign"/> consumes.</summary>
    public IReadOnlyList<string> SortedWorkerIds()
    {
        lock (_gate)
        {
            return _workers.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
    }
}
