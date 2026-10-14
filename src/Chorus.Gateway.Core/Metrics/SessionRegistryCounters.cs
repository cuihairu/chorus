namespace Chorus.Gateway.Core.Metrics;

/// <summary>
/// Registry-level session lifecycle counters, fed into the <c>/metrics</c>
/// surface by the hosts. These describe sessions that never produced (refused
/// at start) or no longer have (evicted) actor diagnostics, so they cannot be
/// derived from <c>SessionDiagnostics</c> or the <c>/sessions</c> surface —
/// this is their only exposition.
/// </summary>
public sealed record SessionRegistryCounters(
    long StartFailures = 0,
    long IdleEvictions = 0,
    long CapacityEvictions = 0)
{
    public static readonly SessionRegistryCounters None = new();
}
