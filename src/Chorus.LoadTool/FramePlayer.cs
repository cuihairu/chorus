namespace Chorus.LoadTool;

/// <summary>
/// Client-side frame playback state machine (frame-sync P1 acceptance).
/// Snapshots are keyed by frame number = tickId: a frame at or below the
/// last applied id is dropped, so a replayed window can never re-render an
/// already applied frame, and a jump over more than one frame is reported as
/// a gap the owner answers with a <c>resync</c> pull (frame-sync P2).
/// This is the quickstart skeleton's render loop as real, tested code.
/// </summary>
public sealed class FramePlayer
{
    private long _lastApplied = -1;

    /// <summary>Last frame id handed to the renderer (-1 before the first frame).</summary>
    public long LastAppliedFrameId => Interlocked.Read(ref _lastApplied);

    /// <summary>
    /// Decides whether an incoming snapshot frame must be rendered and where
    /// the first missing frame is:
    /// <list type="bullet">
    /// <item>contiguous or very first frame → apply, <see cref="FrameDecision.GapFrom"/> = -1</item>
    /// <item>frame beyond the next expected → apply (jump ahead) and report the first missing id</item>
    /// <item>frame at or below the applied id → drop, applied id unchanged</item>
    /// </list>
    /// </summary>
    public FrameDecision Accept(long frameId)
    {
        var last = Interlocked.Read(ref _lastApplied);
        if (frameId <= last)
        {
            return FrameDecision.Dropped;
        }

        var gapFrom = last >= 0 && frameId > last + 1 ? last + 1 : -1;
        Interlocked.Exchange(ref _lastApplied, frameId);
        return new FrameDecision(true, gapFrom);
    }
}

/// <summary>
/// One frame decision: whether the frame must be rendered, plus the first
/// missing frame id when the frame jumped over a gap (-1 when contiguous).
/// </summary>
public readonly record struct FrameDecision(bool Apply, long GapFrom)
{
    /// <summary>Stale or duplicate frame: drop it, nothing to send.</summary>
    public static FrameDecision Dropped { get; } = new(false, -1);
}
