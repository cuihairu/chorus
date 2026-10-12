namespace Chorus.Engine.Session;

public interface ISessionClient
{
    string ClientId { get; }

    ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Preferred send: a snapshot already encoded to its wire bytes (serialized
    /// once per tick for the broadcast path). Default bridges to the
    /// object-based send for transports that re-encode themselves (e.g. the
    /// IPC bus client wraps the snapshot into an envelope).
    /// </summary>
    ValueTask SendSnapshotAsync(WireSnapshot wire, CancellationToken cancellationToken)
        => SendSnapshotAsync(wire.Snapshot, cancellationToken);
}
