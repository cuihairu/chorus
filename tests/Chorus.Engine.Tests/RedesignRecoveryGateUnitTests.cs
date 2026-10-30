using Chorus.GameServer;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The recovery concurrency gate (FIX-PERF-08) as unit evidence: a bounded number of
/// recoveries' heavy work runs at once while the rest wait in a bounded queue, a recovery
/// past the queue is refused explicitly — never silently accepted — a released slot admits
/// the next queued recovery, and an invalid bound fails closed.
/// </summary>
public sealed class RedesignRecoveryGateUnitTests
{
    [Fact]
    public async Task ASetNumberOfRecoveriesRunAtOnce()
    {
        var gate = new RecoveryConcurrencyGate(maxConcurrent: 2, maxQueued: 10);
        var first = await gate.EnterAsync(CancellationToken.None);
        var second = await gate.EnterAsync(CancellationToken.None);
        // Both slots are held; a third must queue, not run.
        var third = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompletedSuccessfully);
        // A released slot admits the queued recovery immediately.
        second.Dispose();
        var granted = await third;
        granted.Dispose();
        first.Dispose();
    }

    [Fact]
    public async Task APastTheQueueBoundRecoveryIsRefusedExplicitly()
    {
        // One slot, no queue: a second concurrent recovery is refused — ResourceExhausted at
        // the gRPC boundary — never silently accepted and never left to run unbounded.
        var gate = new RecoveryConcurrencyGate(maxConcurrent: 1, maxQueued: 0);
        var first = await gate.EnterAsync(CancellationToken.None);
        await Assert.ThrowsAsync<RecoveryQueueFullException>(
            () => gate.EnterAsync(CancellationToken.None).AsTask());
        first.Dispose();
    }

    [Fact]
    public async Task TheQueueBoundCountsOnlyWaiters()
    {
        // Two slots, one waiter allowed: fill both slots, let one queue, then refuse the next.
        var gate = new RecoveryConcurrencyGate(maxConcurrent: 2, maxQueued: 1);
        var first = await gate.EnterAsync(CancellationToken.None);
        var second = await gate.EnterAsync(CancellationToken.None);
        var queued = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queued.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<RecoveryQueueFullException>(
            () => gate.EnterAsync(CancellationToken.None).AsTask());
        second.Dispose();
        var granted = await queued;
        granted.Dispose();
        first.Dispose();
    }

    [Fact]
    public async Task AReleasedSlotAdmitsTheNextQueuedRecovery()
    {
        var gate = new RecoveryConcurrencyGate(maxConcurrent: 1, maxQueued: 5);
        var first = await gate.EnterAsync(CancellationToken.None);
        var queued = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queued.IsCompletedSuccessfully);
        first.Dispose();
        (await queued).Dispose();
    }

    [Fact]
    public void InvalidBoundsFailClosed()
    {
        // A mistyped bound is a configuration error, not a silently unlimited host.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecoveryConcurrencyGate(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecoveryConcurrencyGate(1, -1));
        // A zero queue is the explicit "reject at the concurrency limit" state, not accidental.
        Assert.NotNull(new RecoveryConcurrencyGate(1, 0));
    }
}
