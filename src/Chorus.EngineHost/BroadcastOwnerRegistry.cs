using System.Collections.Concurrent;

namespace Chorus.EngineHost;

/// <summary>
/// Per-session broadcast-owner election (docs/design/shared-memory-transport.md
/// alt 1): the first client of a session owns publishing broadcast snapshots,
/// so the engine sends one eng→gw snapshot per session per tick instead of one
/// per client. Elections and transfers happen on the IPC receive loop; the
/// read side is the session actors' tick threads, hence the concurrent map.
/// </summary>
public sealed class BroadcastOwnerRegistry
{
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

    public void OnConnect(string sessionId, string clientId)
    {
        _owners.TryAdd(sessionId, clientId);
    }

    public bool IsOwner(string sessionId, string clientId)
        => _owners.TryGetValue(sessionId, out var owner)
            && string.Equals(owner, clientId, StringComparison.Ordinal);

    /// <summary>
    /// Hands broadcast duty to another remaining client when the owner leaves,
    /// or clears it when none remain. Called with the client already removed
    /// from the live client set.
    /// </summary>
    public void OnDisconnect(string sessionId, string clientId, IEnumerable<string> remainingClientIds)
    {
        if (!IsOwner(sessionId, clientId))
        {
            return;
        }

        var next = remainingClientIds.FirstOrDefault(id => !string.Equals(id, clientId, StringComparison.Ordinal));
        if (next is null)
        {
            _owners.TryRemove(sessionId, out _);
        }
        else
        {
            _owners[sessionId] = next;
        }
    }
}
