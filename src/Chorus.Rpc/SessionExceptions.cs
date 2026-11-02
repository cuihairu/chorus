namespace Chorus.Rpc;

/// <summary>The session is revoked, expired or otherwise unusable; the caller must not retry with it.</summary>
public sealed class SessionRevokedException : Exception;

/// <summary>A newer login took over; this session can no longer rotate or act.</summary>
public sealed class SessionReplacedException : Exception;

/// <summary>The live session or gameplay binding is not available to this caller.</summary>
public sealed class BindingDeniedException : Exception;

/// <summary>The live session still holds a live round participation (FIX-END-17): a re-mount
/// while the previous round is live would build a second room for a streamer already in one,
/// so the new mount generation is refused until the round releases (architecture 6.2).</summary>
public sealed class MountInLiveRoundException : Exception;

/// <summary>Another external operation is executing on the same mount; the caller must wait.</summary>
public sealed class TaskBusyException : Exception;

/// <summary>The platform task is not in a state that accepts this transition from this caller.</summary>
public sealed class TaskStateConflictException : Exception;

/// <summary>The task is bound to a standing end operation whose barrier has not authorized
/// it yet (FIX-END-09): no attempt may begin before the ladder reaches
/// PlatformTaskAuthorized.</summary>
public sealed class TaskBarrierException : Exception;
