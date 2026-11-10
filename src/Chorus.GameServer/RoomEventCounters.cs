using Chorus.Rpc;

namespace Chorus.GameServer;

/// <summary>
/// The room face's in-process counters (FIX-OBS-11): one Interlocked bump at each decision
/// the session face makes on an event, named by the dictionary metrics (FIX-OBS-01). An
/// event the push leg seats counts ingested; an event the inbox pull hands the tick counts
/// ingested with its batch; a frame whose commit succeeded counts its items applied; a
/// seat the face refused counts dropped. Cumulative since the process started — a restart
/// resets the series, and a recovered room's historical replay never rides this face (the
/// replay derives state from the store's truth, not through the live tick loop). The
/// merged family stays zero until a redesign-side pending batch exists to merge into (the
/// like merge lives inside game modules; there is nothing at the face to count yet) — the
/// family is counted from the start so a later merge lands on a stable API.
/// <c>AppCounterReporter</c> carries the snapshot to the control plane every report
/// interval.
/// </summary>
public sealed class RoomEventCounters
{
    private long _ingested;
    private long _applied;
    private long _merged;
    private long _dropped;

    public void Ingested(long count = 1) => Interlocked.Add(ref _ingested, count);
    public void Applied(long count) => Interlocked.Add(ref _applied, count);
    public void Merged(long count) => Interlocked.Add(ref _merged, count);
    public void Dropped(long count = 1) => Interlocked.Add(ref _dropped, count);

    /// <summary>The families and their cumulative values, in dictionary order — the read
    /// face answers the same names, so the reporter and the API never drift apart.</summary>
    public IReadOnlyList<(string Family, long Value)> Snapshot() =>
    [
        (OpsMetrics.EventsIngestedTotal.Name, Interlocked.Read(ref _ingested)),
        (OpsMetrics.EventsAppliedTotal.Name, Interlocked.Read(ref _applied)),
        (OpsMetrics.EventsMergedTotal.Name, Interlocked.Read(ref _merged)),
        (OpsMetrics.EventsDroppedTotal.Name, Interlocked.Read(ref _dropped)),
    ];
}
