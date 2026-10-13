namespace Chorus.Protocol.Messages;

/// <summary>
/// Per-event frame attribution (frame-sync.md §4.2 P2): each event carries the
/// frameId (tickId) it was applied on, so a client can place events on the
/// render timeline without parsing game state.
/// </summary>
public sealed record FrameEvent(
    long FrameId,
    string EventId,
    string Kind,
    string UserId
);
