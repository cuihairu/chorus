using Cohort.Adapters.Platforms;
using Microsoft.Extensions.Caching.Memory;

namespace Cohort.Engine.Tests;

public sealed class EventDeduplicatorTests
{
    private static EventDeduplicator Create(TimeSpan? ttl = null)
        => new(new MemoryCache(new MemoryCacheOptions()), ttl ?? TimeSpan.FromMinutes(5));

    [Fact]
    public void TryMark_FirstMark_ReturnsTrue_SecondMark_ReturnsFalse()
    {
        var dedup = Create();

        Assert.True(dedup.TryMark(PlatformNames.Douyin, "dy-1"));
        Assert.False(dedup.TryMark(PlatformNames.Douyin, "dy-1"));
    }

    [Fact]
    public void TryMark_DifferentPlatforms_SameEventId_BothAccepted()
    {
        var dedup = Create();

        Assert.True(dedup.TryMark(PlatformNames.Douyin, "shared-1"));
        Assert.True(dedup.TryMark(PlatformNames.Kuaishou, "shared-1"));
    }

    [Fact]
    public void TryMark_DistinctEventIds_BothAccepted()
    {
        var dedup = Create();

        Assert.True(dedup.TryMark(PlatformNames.Douyin, "dy-1"));
        Assert.True(dedup.TryMark(PlatformNames.Douyin, "dy-2"));
    }

    [Fact]
    public void TryMark_BlankInputs_AreAcceptedWithoutMarking()
    {
        var dedup = Create();

        Assert.True(dedup.TryMark(PlatformNames.Douyin, ""));
        Assert.True(dedup.TryMark("", "e1"));
    }

    [Fact]
    public void TryMark_ExpiredEntry_AcceptedAgain()
    {
        var dedup = Create(TimeSpan.FromMilliseconds(50));

        Assert.True(dedup.TryMark(PlatformNames.Douyin, "dy-1"));
        Thread.Sleep(120);

        Assert.True(dedup.TryMark(PlatformNames.Douyin, "dy-1"));
    }
}
