using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Frame-sync P1 acceptance (客户端按帧号渲染): the quickstart skeleton's
/// render loop as real tested code - newest frame wins, stale and duplicate
/// frames drop so a replayed window never re-renders, and a jump over more
/// than one frame reports the first missing id for a resync pull.
/// </summary>
public sealed class FramePlayerTests
{
    [Fact]
    public void FirstFrame_Applies_WithNoGap()
    {
        var player = new FramePlayer();
        Assert.Equal(-1, player.LastAppliedFrameId);

        var decision = player.Accept(1);

        Assert.True(decision.Apply);
        Assert.Equal(-1, decision.GapFrom);
        Assert.Equal(1, player.LastAppliedFrameId);
    }

    [Fact]
    public void ContiguousFrames_AllApply_WithNoGap()
    {
        var player = new FramePlayer();

        foreach (var frameId in new long[] { 1, 2, 3, 4 })
        {
            var decision = player.Accept(frameId);
            Assert.True(decision.Apply);
            Assert.Equal(-1, decision.GapFrom);
        }

        Assert.Equal(4, player.LastAppliedFrameId);
    }

    [Fact]
    public void StaleAndDuplicateFrames_Drop_WithoutMovingTheAppliedId()
    {
        var player = new FramePlayer();
        player.Accept(5);

        Assert.False(player.Accept(5).Apply);
        Assert.False(player.Accept(3).Apply);
        Assert.False(player.Accept(0).Apply);
        Assert.Equal(5, player.LastAppliedFrameId);
    }

    [Fact]
    public void Jump_AppliesTheFrame_AndReportsTheFirstMissingId()
    {
        var player = new FramePlayer();
        player.Accept(10);

        var decision = player.Accept(14);

        Assert.True(decision.Apply);
        Assert.Equal(11, decision.GapFrom);
        Assert.Equal(14, player.LastAppliedFrameId);
    }

    [Fact]
    public void ReplayedFramesDuringGapFill_DropAsStale()
    {
        // The server replies with the retained window [11..14]; the client has
        // already jumped ahead to 14, so every replayed frame must be dropped
        // (idempotent by frame id) and the applied id must not regress.
        var player = new FramePlayer();
        player.Accept(10);
        Assert.True(player.Accept(14).Apply);

        foreach (var frameId in new long[] { 11, 12, 13 })
        {
            Assert.False(player.Accept(frameId).Apply);
        }

        Assert.Equal(14, player.LastAppliedFrameId);
    }

    [Fact]
    public void JumpBeforeAnyFrameApplied_HasNoGap()
    {
        var player = new FramePlayer();

        var decision = player.Accept(7);

        Assert.True(decision.Apply);
        Assert.Equal(-1, decision.GapFrom);
    }

    [Fact]
    public void DroppedDecision_CarriesNoGap()
    {
        Assert.False(FrameDecision.Dropped.Apply);
        Assert.Equal(-1, FrameDecision.Dropped.GapFrom);
    }
}
