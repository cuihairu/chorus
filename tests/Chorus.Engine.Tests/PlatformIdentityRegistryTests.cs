using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// 平台身份映射金测（todo §14 身份映射项，契约 = 总体架构审计核心组件 Identity：
/// LiveCapabilities → Identity，平台账号 → chorus 身份）。与 PlatformBindingRegistry
/// 同纪律：冲突 fail-visible 不隐式覆盖、换绑/解绑 CAS、未命中返回 null。
/// </summary>
public sealed class PlatformIdentityRegistryTests
{
    private static PlatformIdentityRegistry NewRegistry()
        => new();

    private static readonly PlatformIdentity StreamerA = new(PlatformIdentityKind.Streamer, "streamer-a");
    private static readonly PlatformIdentity StreamerB = new(PlatformIdentityKind.Streamer, "streamer-b");

    [Fact]
    public void Map_NewAccount_Resolves()
    {
        var registry = NewRegistry();

        var ok = registry.TryMap("bilibili", "ou-777", StreamerA, out var existing);

        Assert.True(ok);
        Assert.Null(existing);
        Assert.Equal(StreamerA, registry.Resolve("bilibili", "ou-777"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Map_SameIdentityTwice_Idempotent()
    {
        var registry = NewRegistry();
        registry.TryMap("bilibili", "ou-777", StreamerA, out _);

        var ok = registry.TryMap("bilibili", "ou-777", StreamerA, out var existing);

        Assert.True(ok);
        Assert.Equal(StreamerA, existing);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Map_Conflict_FailsFailVisible()
    {
        var registry = NewRegistry();
        registry.TryMap("bilibili", "ou-777", StreamerA, out _);

        var ok = registry.TryMap("bilibili", "ou-777", StreamerB, out var existing);

        // 一个平台账号同时只对应一个 chorus 身份；换身份必须走 TryReplace。
        Assert.False(ok);
        Assert.Equal(StreamerA, existing);
        Assert.Equal(StreamerA, registry.Resolve("bilibili", "ou-777"));
    }

    [Fact]
    public void Resolve_UnknownAccount_ReturnsNull()
    {
        var registry = NewRegistry();

        Assert.Null(registry.Resolve("bilibili", "nobody"));
    }

    [Fact]
    public void Map_PlatformCaseInsensitive()
    {
        var registry = NewRegistry();
        registry.TryMap("bilibili", "ou-777", StreamerA, out _);

        Assert.Equal(StreamerA, registry.Resolve("BILIBILI", "ou-777"));
    }

    [Fact]
    public void Replace_WithCurrent_Moves()
    {
        var registry = NewRegistry();
        registry.TryMap("bilibili", "ou-777", StreamerA, out _);

        var ok = registry.TryReplace("bilibili", "ou-777", expected: StreamerA, replacement: StreamerB, out var actual);

        Assert.True(ok);
        Assert.Null(actual);
        Assert.Equal(StreamerB, registry.Resolve("bilibili", "ou-777"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Replace_WrongExpected_FailsFailVisible()
    {
        var registry = NewRegistry();
        registry.TryMap("bilibili", "ou-777", StreamerA, out _);

        var ok = registry.TryReplace("bilibili", "ou-777", expected: StreamerB, replacement: StreamerA, out var actual);

        Assert.False(ok);
        Assert.Equal(StreamerA, actual);
        Assert.Equal(StreamerA, registry.Resolve("bilibili", "ou-777"));
    }

    [Fact]
    public void Replace_NoMapping_FailsFailVisible()
    {
        var registry = NewRegistry();

        var ok = registry.TryReplace("bilibili", "ou-777", expected: StreamerA, replacement: StreamerB, out var actual);

        Assert.False(ok);
        Assert.Null(actual);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Unbind_WithCurrent_Removes()
    {
        var registry = NewRegistry();
        registry.TryMap("bilibili", "ou-777", StreamerA, out _);

        var ok = registry.TryUnbind("bilibili", "ou-777", expected: StreamerA, out var actual);

        Assert.True(ok);
        Assert.Null(actual);
        Assert.Null(registry.Resolve("bilibili", "ou-777"));
        Assert.True(registry.TryMap("bilibili", "ou-777", StreamerB, out _));
    }

    [Fact]
    public void Unbind_WrongExpected_DoesNotRemoveNewMapping()
    {
        var registry = NewRegistry();
        registry.TryMap("bilibili", "ou-777", StreamerA, out _);
        registry.TryReplace("bilibili", "ou-777", StreamerA, StreamerB, out _);

        // 迟到的解绑不能删掉换绑后的新身份。
        var ok = registry.TryUnbind("bilibili", "ou-777", expected: StreamerA, out var actual);

        Assert.False(ok);
        Assert.Equal(StreamerB, actual);
        Assert.Equal(StreamerB, registry.Resolve("bilibili", "ou-777"));
    }

    [Fact]
    public void Snapshot_ListsMappings()
    {
        var registry = NewRegistry();
        registry.TryMap("bilibili", "ou-1", StreamerA, out _);
        registry.TryMap("douyin", "dy-1", new PlatformIdentity(PlatformIdentityKind.Player, "player-dy-1"), out _);

        var snapshot = registry.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, m => m.Platform == "bilibili" && m.PlatformAccountId == "ou-1" && m.Identity.Equals(StreamerA));
        Assert.Contains(snapshot, m => m.Platform == "douyin" && m.PlatformAccountId == "dy-1");
    }

    [Fact]
    public void ConcurrentMaps_ExactlyOneWins()
    {
        var registry = NewRegistry();
        var identities = new[] { StreamerA, StreamerB };
        var winners = new System.Collections.Concurrent.ConcurrentBag<PlatformIdentity>();

        Parallel.ForEach(identities, identity =>
        {
            if (registry.TryMap("bilibili", "ou-777", identity, out _))
            {
                winners.Add(identity);
            }
        });

        Assert.Single(winners);
        Assert.Equal(winners.Single(), registry.Resolve("bilibili", "ou-777"));
        Assert.Equal(1, registry.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Map_InvalidIdentityId_Throws(string? id)
    {
        var registry = NewRegistry();

        Assert.ThrowsAny<ArgumentException>(
            () => registry.TryMap("bilibili", "ou-1", new PlatformIdentity(PlatformIdentityKind.Player, id!), out _));
    }
}
