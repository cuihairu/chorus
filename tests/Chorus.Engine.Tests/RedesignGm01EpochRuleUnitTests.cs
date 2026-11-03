using Chorus.ControlServer;

namespace Chorus.Engine.Tests;

/// <summary>
/// The room epoch rule (FIX-GM-01, architecture 4.5): the epoch is a per-room ownership
/// generation, not a process-level counter, and the bump arithmetic is strict — an omitted
/// target advances by one, a stated target must be strictly greater than the expected epoch,
/// and a move that would lower, hold, or overflow the epoch is an invalid request. The
/// identifier check bounds the request shape so an empty tenant/game/room is rejected as an
/// invalid request, never a lookup miss.
/// </summary>
public sealed class RedesignGm01EpochRuleUnitTests
{
    [Fact]
    public void AnOmittedTargetAdvancesByOne()
    {
        // The proto3 absent default is 0 (TargetUnspecified); the sentinel is unambiguous
        // because a real bump target is always >= 2.
        Assert.Equal(4, RoomEpochRule.ResolveTarget(expected: 3, target: 0));
        Assert.Equal(2, RoomEpochRule.ResolveTarget(expected: 1, target: 0));
        Assert.Equal(long.MaxValue, RoomEpochRule.ResolveTarget(expected: long.MaxValue - 1, target: 0));
    }

    [Fact]
    public void AStatedTargetIsUsedVerbatimWhenItIsStrictlyGreater()
    {
        // A stated target may jump more than one; the room lands exactly there.
        Assert.Equal(7, RoomEpochRule.ResolveTarget(expected: 3, target: 7));
        Assert.Equal(4, RoomEpochRule.ResolveTarget(expected: 3, target: 4));
    }

    [Fact]
    public void AnEqualOrLowerTargetIsRejected()
    {
        // Holding still is not a bump; a lower or negative move would reset history.
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomEpochRule.ResolveTarget(expected: 3, target: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomEpochRule.ResolveTarget(expected: 3, target: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomEpochRule.ResolveTarget(expected: 3, target: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomEpochRule.ResolveTarget(expected: 1, target: 1));
    }

    [Fact]
    public void AnOverflowingAdvanceIsRejected()
    {
        // The last epoch cannot advance by one; the request is invalid, never wrapped.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoomEpochRule.ResolveTarget(expected: long.MaxValue, target: 0));
    }

    [Fact]
    public void AnEpochBelowTheFloorIsRejected()
    {
        // The initial ownership row is created at 1, so 0 is never a valid expected epoch —
        // and it is also why 0 can safely serve as the "omit the target" sentinel.
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomEpochRule.ResolveTarget(expected: 0, target: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomEpochRule.ResolveTarget(expected: -5, target: 0));
    }

    [Fact]
    public void IncompleteIdentifiersAreRejectedAsInvalidRequests()
    {
        // The scope names bound the operation to the caller's own ownership (FIX-GM-06); an
        // empty one is an invalid request, never a "not found" — the caller must fix the shape.
        Assert.Throws<ArgumentException>(() => RoomEpochRule.ValidateIdentifiers("", "game-1", "room-1"));
        Assert.Throws<ArgumentException>(() => RoomEpochRule.ValidateIdentifiers("tenant-a", "", "room-1"));
        Assert.Throws<ArgumentException>(() => RoomEpochRule.ValidateIdentifiers("tenant-a", "game-1", ""));
        Assert.Throws<ArgumentException>(() => RoomEpochRule.ValidateIdentifiers(" ", "game-1", "room-1"));
        // A null name is a different exception (ArgumentNullException is a subtype, but this
        // runner's Assert.Throws demands the exact type) — still an invalid request.
        Assert.Throws<ArgumentNullException>(() => RoomEpochRule.ValidateIdentifiers("tenant-a", null!, "room-1"));
    }
}
