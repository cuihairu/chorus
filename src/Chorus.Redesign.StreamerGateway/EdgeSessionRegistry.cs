namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The in-memory connection index. The gateway owns no durable state (architecture: it loads
/// no long-lived business entities), so this registry is exactly what a gateway crash loses —
/// connections — while every session, binding and room lives on in the stores. Session
/// lookups arrive with the session-replaced notice (M4-2); connection identity is what the
/// notice carries, so the index stays keyed by connection id.
/// </summary>
public sealed class EdgeSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EdgeConnection> _byConnectionId = new(StringComparer.Ordinal);
    private readonly int _maxConnections;

    public EdgeSessionRegistry(int maxConnections) => _maxConnections = maxConnections;

    public int Count { get { lock (_gate) return _byConnectionId.Count; } }

    /// <summary>Registers a connection, refusing when the gateway is at its connection
    /// budget — overload must close visibly, not degrade silently.</summary>
    public bool TryOpen(EdgeConnection connection)
    {
        lock (_gate)
        {
            if (_byConnectionId.Count >= _maxConnections) return false;
            return _byConnectionId.TryAdd(connection.ConnectionId, connection);
        }
    }

    public void Close(EdgeConnection connection)
    {
        lock (_gate) _byConnectionId.Remove(connection.ConnectionId);
    }

    public EdgeConnection? FindByConnection(string connectionId)
    {
        lock (_gate) return _byConnectionId.TryGetValue(connectionId, out var connection)
            ? connection : null;
    }
}
