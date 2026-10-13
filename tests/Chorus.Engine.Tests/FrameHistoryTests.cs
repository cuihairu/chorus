using Chorus.Engine.Session;

namespace Chorus.Engine.Tests;

/// <summary>
/// Frame-sync P2 acceptance (帧历史 ring buffer): byte-capped retention with
/// oldest-first eviction, and window queries that refuse partial replays -
/// a window whose start was evicted is a miss so the caller answers with the
/// latest snapshot plus forced=true instead of a replay with a hidden gap.
/// </summary>
public sealed class FrameHistoryTests
{
    private static WireSnapshot Wire(long tickId, int bytes) => WireSnapshot.Encode(
        new SessionSnapshot("s", tickId, 0, new { pad = new string('x', Math.Max(0, bytes - 64)) }));

    private static int SizeOf(long tickId, int approxBytes)
    {
        // Encoded JSON length for the pad trick above; exact length matters
        // only via the cap comparisons in each test.
        return WireSnapshot.Encode(new SessionSnapshot("s", tickId, 0, new { pad = new string('x', Math.Max(0, approxBytes - 64)) })).EncodedJson.Length;
    }

    [Fact]
    public void Empty_HasNoWindow_AndMissesAnyQuery()
    {
        var history = new FrameHistory(1_000_000);
        Assert.Equal(-1, history.OldestFrameId);
        Assert.Equal(-1, history.NewestFrameId);
        Assert.False(history.TryGetWindow(0, out var frames));
        Assert.Empty(frames);
    }

    [Fact]
    public void Add_ThenWindowFromOldest_ReturnsAllFramesInOrder()
    {
        var history = new FrameHistory(1_000_000);
        history.Add(Wire(1, 200));
        history.Add(Wire(2, 200));
        history.Add(Wire(3, 200));

        Assert.Equal(1, history.OldestFrameId);
        Assert.Equal(3, history.NewestFrameId);

        Assert.True(history.TryGetWindow(1, out var frames));
        Assert.Equal(new long[] { 1, 2, 3 }, frames.Select(f => f.Snapshot.TickId));

        Assert.True(history.TryGetWindow(2, out var tail));
        Assert.Equal(new long[] { 2, 3 }, tail.Select(f => f.Snapshot.TickId));
    }

    [Fact]
    public void WindowStart_BeforeOldest_IsAMiss()
    {
        var history = new FrameHistory(1_000_000);
        history.Add(Wire(5, 200));
        history.Add(Wire(6, 200));

        // Frames 1-4 were never stored: answering [1..] from the retained tail
        // would silently skip a gap, so the query must miss.
        Assert.False(history.TryGetWindow(1, out _));
        Assert.False(history.TryGetWindow(4, out _));
        Assert.True(history.TryGetWindow(5, out _));
    }

    [Fact]
    public void Eviction_DropsOldestFirst_WithinByteCap()
    {
        var frameSize = SizeOf(1, 200);
        // Two frames fit, three do not.
        var history = new FrameHistory(frameSize * 2 + 1);
        history.Add(Wire(1, 200));
        history.Add(Wire(2, 200));
        Assert.Equal(2, history.Count);

        history.Add(Wire(3, 200));
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history.OldestFrameId);
        Assert.Equal(3, history.NewestFrameId);
        Assert.True(history.Bytes <= frameSize * 2 + 1);
        Assert.False(history.TryGetWindow(1, out _));
        Assert.True(history.TryGetWindow(2, out _));
    }

    [Fact]
    public void OversizeSingleFrame_IsRetainedAsLatest_NeverEvictsTheNewcomer()
    {
        var history = new FrameHistory(100);
        history.Add(Wire(1, 200));
        history.Add(Wire(2, 5000)); // Larger than the whole cap.

        Assert.Equal(1, history.Count);
        Assert.Equal(2, history.OldestFrameId);
        Assert.Equal(2, history.NewestFrameId);
        Assert.True(history.TryGetWindow(2, out _));
    }

    [Fact]
    public void ZeroCapacity_DisablesHistory()
    {
        var history = new FrameHistory(0);
        history.Add(Wire(1, 200));
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.Bytes);
        Assert.False(history.TryGetWindow(1, out _));
    }

    [Fact]
    public void WindowQuery_ReturnsRetainedRangeCopy_IndependentOfLaterMutation()
    {
        var history = new FrameHistory(1_000_000);
        history.Add(Wire(1, 200));
        history.Add(Wire(2, 200));
        Assert.True(history.TryGetWindow(1, out var frames));
        Assert.Equal(2, frames.Count);

        history.Add(Wire(3, 200));
        Assert.Equal(2, frames.Count);
    }
}
