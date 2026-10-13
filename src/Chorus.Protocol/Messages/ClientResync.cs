namespace Chorus.Protocol.Messages;

/// <summary>
/// Client-initiated gap-fill request (frame-sync.md §4.3 P2): the client sends
/// the last frameId it applied, and the server replies with all snapshots in
/// [fromFrameId, currentTickId] from its frame history ring buffer.
/// </summary>
public sealed record ClientResync(
    string Type,
    string SessionId,
    string ClientId,
    long FromFrameId
);
