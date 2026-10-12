namespace Chorus.Engine.Routing;

/// <summary>
/// Room stickiness rule (docs/design/process-topology.md §5.1, stage-3 code
/// half): every input of one room must be ordered inside a single actor, so a
/// room can never have two writers. This pins the mapping a multi-worker
/// deployment enforces as a pure function; the room-manager publishes its
/// result as the shared route table (<see cref="RoomRouteTable"/>, §5.3).
/// Today's single worker is the degenerate case — every room maps to it.
/// No production wiring yet: consumers land with stage 3.
/// </summary>
public static class RoomAffinity
{
    /// <summary>
    /// The one worker owning <paramref name="sessionId"/> among the current
    /// worker set, or null when no worker exists (caller must not route).
    /// Depends only on the worker SET (sorted before indexing), never on the
    /// order the set was given in, so every component computing it agrees.
    /// </summary>
    public static string? Assign(string sessionId, IReadOnlyCollection<string> workerIds)
    {
        if (workerIds.Count == 0)
        {
            return null;
        }

        var sorted = workerIds.OrderBy(w => w, StringComparer.Ordinal).ToArray();
        return sorted[(uint)StableHash(sessionId) % (uint)sorted.Length];
    }

    /// <summary>FNV-1a: stable across processes (string.GetHashCode is not).</summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return (int)hash;
        }
    }
}

/// <summary>
/// The shared 房间 → worker map (docs/design/process-topology.md §5.3): a
/// room-manager pushes whole tables, platform-gw and streamer-gw read the same
/// one. Replaces are whole-map swaps so readers always see a consistent table.
/// </summary>
public sealed class RoomRouteTable
{
    private IReadOnlyDictionary<string, string> _routes = Empty;

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public int Count => _routes.Count;

    public bool TryGetWorker(string sessionId, out string? workerId)
        => _routes.TryGetValue(sessionId, out workerId);

    /// <summary>A consistent view of the current table; safe to hand to readers.</summary>
    public IReadOnlyDictionary<string, string> Snapshot() => _routes;

    /// <summary>Land a pushed table atomically; partial updates never show.</summary>
    public void Replace(IReadOnlyDictionary<string, string> routes)
    {
        _routes = new Dictionary<string, string>(routes, StringComparer.Ordinal);
    }
}
