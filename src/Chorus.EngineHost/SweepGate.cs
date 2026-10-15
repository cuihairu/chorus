namespace Chorus.EngineHost;

/// <summary>
/// Cadence gate for the engine's amortized idle sweep (the EngineIpcService
/// receive-loop trigger). Below the session cap the gate opens every 30s; at
/// the cap it tightens to 1s so dead client-less streams free room for new
/// ones without waiting a full idle interval — but never per message: the
/// 2026-10-15 gate reproduction collapsed event delivery to ~5% because the
/// trigger fired a full sweep on EVERY message once sessions reached the cap
/// (800=800 at gate scale), drowning the single receive loop in sweep work.
/// </summary>
public sealed class SweepGate(int maxSessions)
{
    public const long IdleIntervalMs = 30_000;
    public const long CapacityIntervalMs = 1_000;

    private readonly int _maxSessions = maxSessions;
    private long _lastSweepMs;

    /// <summary>Whether a sweep should run now. maxSessions &lt;= 0 means the
    /// session count is uncapped and the cadence never tightens.</summary>
    public bool ShouldSweep(long nowMs, int sessionCount)
    {
        var interval = _maxSessions > 0 && sessionCount >= _maxSessions ? CapacityIntervalMs : IdleIntervalMs;
        var last = Interlocked.Read(ref _lastSweepMs);
        if (last != 0 && nowMs - last < interval)
        {
            return false;
        }

        Interlocked.Exchange(ref _lastSweepMs, nowMs);
        return true;
    }
}
