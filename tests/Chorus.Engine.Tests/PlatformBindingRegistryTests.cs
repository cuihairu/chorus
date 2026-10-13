using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// 平台账号唯一绑定金测（todo §14 首项，契约 = docs/design/overall-architecture.md §4.1）：
/// UNIQUE(Platform, PlatformAccountId)、换绑防并发双绑（CAS）、找不到唯一绑定不猜测
/// （Resolve 返回 null 由调用方 fail-visible，本注册表绝不广播）。
/// </summary>
public sealed class PlatformBindingRegistryTests
{
    private static PlatformBindingRegistry NewRegistry()
        => new();

    [Fact]
    public void Bind_NewAccount_BindsAndResolves()
    {
        var registry = NewRegistry();

        var ok = registry.TryBind("bilibili", "ou-777", "room-1", out var conflict);

        Assert.True(ok);
        Assert.Null(conflict);
        Assert.Equal("room-1", registry.Resolve("bilibili", "ou-777"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Bind_AlreadyBoundToOtherRoom_FailsFailVisible()
    {
        var registry = NewRegistry();
        registry.TryBind("bilibili", "ou-777", "room-1", out _);

        var ok = registry.TryBind("bilibili", "ou-777", "room-2", out var conflict);

        // 唯一性约束：第二个房间不能悄悄绑上，冲突对调用方可见（不隐式换绑）。
        Assert.False(ok);
        Assert.Equal("room-1", conflict);
        Assert.Equal("room-1", registry.Resolve("bilibili", "ou-777"));
    }

    [Fact]
    public void Bind_PlatformCaseInsensitive_SameAccount()
    {
        var registry = NewRegistry();
        registry.TryBind("bilibili", "ou-777", "room-1", out _);

        // 平台名来自路由/配置，大小写不应分裂出第二份绑定。
        var ok = registry.TryBind("Bilibili", "ou-777", "room-2", out var conflict);

        Assert.False(ok);
        Assert.Equal("room-1", conflict);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Bind_DifferentAccounts_DoNotConflict()
    {
        var registry = NewRegistry();

        Assert.True(registry.TryBind("bilibili", "ou-1", "room-1", out _));
        Assert.True(registry.TryBind("bilibili", "ou-2", "room-2", out _));
        Assert.True(registry.TryBind("douyin", "ou-1", "room-3", out _));

        Assert.Equal("room-3", registry.Resolve("douyin", "ou-1"));
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void Resolve_NoBinding_ReturnsNull()
    {
        var registry = NewRegistry();

        Assert.Null(registry.Resolve("bilibili", "nobody"));
    }

    [Fact]
    public void Rebind_WithCurrentRoom_MovesAtomically()
    {
        var registry = NewRegistry();
        registry.TryBind("bilibili", "ou-777", "room-1", out _);

        var ok = registry.TryRebind("bilibili", "ou-777", expectedRoomId: "room-1", newRoomId: "room-2", out var actual);

        Assert.True(ok);
        Assert.Null(actual);
        // 任何时刻最多一个有效绑定：移动后只剩 room-2。
        Assert.Equal("room-2", registry.Resolve("bilibili", "ou-777"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Rebind_WrongExpectedRoom_FailsFailVisible()
    {
        var registry = NewRegistry();
        registry.TryBind("bilibili", "ou-777", "room-1", out _);

        // 过期的调用者拿着旧房间号换绑 → 拒绝，返回当前真实绑定。
        var ok = registry.TryRebind("bilibili", "ou-777", expectedRoomId: "room-stale", newRoomId: "room-2", out var actual);

        Assert.False(ok);
        Assert.Equal("room-1", actual);
        Assert.Equal("room-1", registry.Resolve("bilibili", "ou-777"));
    }

    [Fact]
    public void Rebind_NoBinding_FailsFailVisible()
    {
        var registry = NewRegistry();

        var ok = registry.TryRebind("bilibili", "ou-777", expectedRoomId: "room-1", newRoomId: "room-2", out var actual);

        Assert.False(ok);
        Assert.Null(actual);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Unbind_WithCurrentRoom_RemovesAndAllowsRebind()
    {
        var registry = NewRegistry();
        registry.TryBind("bilibili", "ou-777", "room-1", out _);

        var ok = registry.TryUnbind("bilibili", "ou-777", expectedRoomId: "room-1", out var actual);

        Assert.True(ok);
        Assert.Null(actual);
        Assert.Null(registry.Resolve("bilibili", "ou-777"));
        Assert.True(registry.TryBind("bilibili", "ou-777", "room-2", out _));
    }

    [Fact]
    public void Unbind_WrongExpectedRoom_DoesNotRemoveNewBinding()
    {
        var registry = NewRegistry();
        registry.TryBind("bilibili", "ou-777", "room-1", out _);
        registry.TryRebind("bilibili", "ou-777", "room-1", "room-2", out _);

        // 旧房间侧迟到的解绑不能删掉换绑后的新绑定。
        var ok = registry.TryUnbind("bilibili", "ou-777", expectedRoomId: "room-1", out var actual);

        Assert.False(ok);
        Assert.Equal("room-2", actual);
        Assert.Equal("room-2", registry.Resolve("bilibili", "ou-777"));
    }

    [Fact]
    public void Snapshot_ListsCurrentBindings()
    {
        var registry = NewRegistry();
        registry.TryBind("bilibili", "ou-1", "room-1", out _);
        registry.TryBind("douyin", "ou-2", "room-2", out _);

        var snapshot = registry.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, b => b.Platform == "bilibili" && b.PlatformAccountId == "ou-1" && b.RoomId == "room-1");
        Assert.Contains(snapshot, b => b.Platform == "douyin" && b.PlatformAccountId == "ou-2" && b.RoomId == "room-2");
    }

    [Fact]
    public void ConcurrentBinds_ExactlyOneWins()
    {
        var registry = NewRegistry();
        var rooms = Enumerable.Range(0, 16).Select(i => $"room-{i}").ToArray();
        var winners = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.ForEach(rooms, room =>
        {
            if (registry.TryBind("bilibili", "ou-777", room, out _))
            {
                winners.Add(room);
            }
        });

        // 唯一性在并发下仍然成立：恰好一个房间绑上。
        Assert.Single(winners);
        Assert.Equal(winners.Single(), registry.Resolve("bilibili", "ou-777"));
        Assert.Equal(1, registry.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Bind_InvalidRoomId_Throws(string? roomId)
    {
        var registry = NewRegistry();

        Assert.ThrowsAny<ArgumentException>(() => registry.TryBind("bilibili", "ou-1", roomId!, out _));
    }
}
