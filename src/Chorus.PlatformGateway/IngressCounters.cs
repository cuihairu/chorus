using Chorus.Rpc;

namespace Chorus.PlatformGateway;

/// <summary>
/// The webhook face's in-process counters (FIX-OBS-10): one Interlocked increment at each
/// disposition the face answers, named by the dictionary metrics (FIX-OBS-01). Cumulative
/// since the process started — a restart resets the series; the control plane's history is
/// where the longer window lives (FIX-OBS-13). The rate-limited family stays zero until an
/// actual limiter stands behind the face; the family is counted from the start so a later
/// limiter lands on a stable API. <c>AppCounterReporter</c> carries the snapshot to the
/// control plane every report interval.
/// </summary>
public sealed class IngressCounters
{
    private long _accepted;
    private long _duplicates;
    private long _unverified;
    private long _unmapped;
    private long _rateLimited;
    private long _tooLarge;
    private long _timedOut;

    public void Accepted() => Interlocked.Increment(ref _accepted);
    public void Duplicates() => Interlocked.Increment(ref _duplicates);
    public void Unverified() => Interlocked.Increment(ref _unverified);
    public void Unmapped() => Interlocked.Increment(ref _unmapped);
    public void RateLimited() => Interlocked.Increment(ref _rateLimited);
    public void TooLarge() => Interlocked.Increment(ref _tooLarge);
    public void TimedOut() => Interlocked.Increment(ref _timedOut);

    /// <summary>The families and their cumulative values, in dictionary order — the read
    /// face answers the same names, so the reporter and the API never drift apart.</summary>
    public IReadOnlyList<(string Family, long Value)> Snapshot() =>
    [
        (OpsMetrics.IngressAcceptedTotal.Name, Interlocked.Read(ref _accepted)),
        (OpsMetrics.IngressDuplicatesTotal.Name, Interlocked.Read(ref _duplicates)),
        (OpsMetrics.IngressUnverifiedTotal.Name, Interlocked.Read(ref _unverified)),
        (OpsMetrics.IngressUnmappedTotal.Name, Interlocked.Read(ref _unmapped)),
        (OpsMetrics.IngressRateLimitedTotal.Name, Interlocked.Read(ref _rateLimited)),
        (OpsMetrics.IngressTooLargeTotal.Name, Interlocked.Read(ref _tooLarge)),
        (OpsMetrics.IngressTimedOutTotal.Name, Interlocked.Read(ref _timedOut)),
    ];
}
