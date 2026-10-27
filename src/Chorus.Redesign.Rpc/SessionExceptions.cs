namespace Chorus.Redesign.Rpc;

/// <summary>The session is revoked, expired or otherwise unusable; the caller must not retry with it.</summary>
public sealed class SessionRevokedException : Exception;

/// <summary>A newer login took over; this session can no longer rotate or act.</summary>
public sealed class SessionReplacedException : Exception;
