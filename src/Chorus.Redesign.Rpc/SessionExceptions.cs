namespace Chorus.Redesign.Rpc;

/// <summary>The session is revoked, expired or otherwise unusable; the caller must not retry with it.</summary>
public sealed class SessionRevokedException : Exception;

/// <summary>A newer login took over; this session can no longer rotate or act.</summary>
public sealed class SessionReplacedException : Exception;

/// <summary>The live session or gameplay binding is not available to this caller.</summary>
public sealed class BindingDeniedException : Exception;

/// <summary>Another external operation is executing on the same mount; the caller must wait.</summary>
public sealed class TaskBusyException : Exception;

/// <summary>The platform task is not in a state that accepts this transition from this caller.</summary>
public sealed class TaskStateConflictException : Exception;
