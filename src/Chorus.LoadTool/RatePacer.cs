using System.Diagnostics;

namespace Chorus.LoadTool;

/// <summary>
/// Absolute-schedule pacer for the ingress producers: send N is due at
/// N × 1000/targetPerSecond milliseconds since construction. Late sends fire
/// immediately (Zero) so a slow transport degrades to blast mode instead of
/// accumulating idle debt - the long-run rate still converges to the target.
///
/// Replaces the previous `1000 / producerRps` integer division, which
/// truncated to 0 above ~1000 events/s per producer and silently disabled
/// pacing (every `--rate` beyond that was aspirational; the driver just sent
/// as fast as responses allowed).
/// </summary>
public sealed class RatePacer
{
    private readonly double _intervalMs;
    private readonly long _startTicks;
    private long _index;

    public RatePacer(int targetPerSecond)
    {
        targetPerSecond = Math.Max(1, targetPerSecond);
        _intervalMs = 1000.0 / targetPerSecond;
        _startTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Delay until the next send's slot; Zero when already behind.</summary>
    public TimeSpan NextDelay()
    {
        var target = TimeSpan.FromMilliseconds(_index * _intervalMs);
        var elapsed = Stopwatch.GetElapsedTime(_startTicks);
        return target > elapsed ? target - elapsed : TimeSpan.Zero;
    }

    public void RecordSend() => _index++;
}
