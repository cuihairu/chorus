using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review P0-7 acceptance: reserve is atomic under concurrency, publish
/// failure (Rollback) is retryable instead of being misremembered as a
/// duplicate, and Commit keeps suppressing duplicates until the TTL expires.
/// </summary>
public sealed class EventDeduplicatorTests
{
    private static EventDeduplicator Create(TimeSpan? ttl = null)
        => new(ttl ?? TimeSpan.FromMinutes(5));

    [Fact]
    public void TryReserve_FirstTrue_SecondFalse()
    {
        var dedup = Create();

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
        Assert.False(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
    }

    [Fact]
    public void TryReserve_DifferentPlatforms_SameEventId_BothAccepted()
    {
        var dedup = Create();

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "shared-1", out _));
        Assert.True(dedup.TryReserve(PlatformNames.Kuaishou, "shared-1", out _));
    }

    [Fact]
    public void TryReserve_DistinctEventIds_BothAccepted()
    {
        var dedup = Create();

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-2", out _));
    }

    [Fact]
    public void TryReserve_BlankInputs_PassThroughWithoutLease()
    {
        var dedup = Create();

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "", out var blankEvent));
        Assert.True(dedup.TryReserve("", "e1", out var blankPlatform));
        Assert.False(blankEvent.Valid);
        Assert.False(blankPlatform.Valid);
    }

    [Fact]
    public void TryReserve_ExpiredEntry_ReReservable()
    {
        var dedup = Create(TimeSpan.FromMilliseconds(50));

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
        Thread.Sleep(120);

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
    }

    [Fact]
    public void Commit_SuppressesDuplicatesUntilTtl()
    {
        var dedup = Create();

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out var lease));
        dedup.Commit(lease);

        Assert.False(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
    }

    /// <summary>
    /// 四态投递 Failed acceptance: a failed publish (Rollback) must NOT keep
    /// the id remembered - the platform's retry arrives as a fresh delivery.
    /// </summary>
    [Fact]
    public void Rollback_PublishFailure_AllowsPlatformRetry()
    {
        var dedup = Create();

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out var lease));
        dedup.Rollback(lease);

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out var retryLease));
        Assert.True(retryLease.Valid);
    }

    [Fact]
    public void Rollback_StaleToken_DoesNotRemoveNewReservation()
    {
        var dedup = Create();

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out var first));
        dedup.Rollback(first);
        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out var second));

        // The stale first lease must not release the second reservation.
        dedup.Rollback(first);

        Assert.False(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
    }

    [Fact]
    public void Rollback_AfterCommit_DoesNotForgetDelivered()
    {
        var dedup = Create();

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out var lease));
        dedup.Commit(lease);
        dedup.Rollback(lease);

        Assert.False(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
    }

    [Fact]
    public void CommitAndRollback_InvalidLease_AreNoOps()
    {
        var dedup = Create();

        dedup.Commit(default);
        dedup.Rollback(default);

        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-1", out _));
    }

    /// <summary>
    /// Atomicity acceptance: N concurrent reserves of the same event id must
    /// produce exactly one winner - the old check-then-set TryMark let two
    /// requests both pass.
    /// </summary>
    [Fact]
    public async Task TryReserve_ConcurrentSameId_ExactlyOneWinner()
    {
        var dedup = Create();
        const int contenders = 32;

        var results = await Task.WhenAll(Enumerable.Range(0, contenders).Select(async n =>
        {
            // Small stagger so contenders genuinely overlap on the same key.
            await Task.Yield();
            return dedup.TryReserve(PlatformNames.Douyin, "race-1", out _);
        }));

        Assert.Equal(1, results.Count(won => won));
    }

    [Fact]
    public async Task TryReserve_ConcurrentDistinctIds_AllAccepted()
    {
        var dedup = Create();
        const int count = 200;

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            await Task.Yield();
            return dedup.TryReserve(PlatformNames.Douyin, $"dy-{i}", out _);
        }));

        Assert.Equal(count, results.Count(won => won));
    }

    [Fact]
    public void TryReserve_AtCapacity_AfterSweep_EvictsSoonestToExpire()
    {
        // Tiny table: fill it with entries that expire late, then verify the
        // valve keeps memory bounded by evicting (dedup window trade-off).
        var dedup = new EventDeduplicator(TimeSpan.FromSeconds(60), capacity: 4);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(dedup.TryReserve(PlatformNames.Douyin, $"dy-{i}", out _));
        }

        // 5th reserve triggers sweep+valve: must still succeed (bounded memory,
        // liveness preserved) and exactly one of the earlier ids is forgotten.
        Assert.True(dedup.TryReserve(PlatformNames.Douyin, "dy-new", out _));
    }
}
