using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// 平台原生榜适配器注册金测（todo §15 排行榜项，契约 = 审计 §9.1 PlatformLeaderboard +
/// §9.2「平台原生排行榜需要符合平台自身 API、权限和数据模型」）：
/// 未注册平台 fail-closed（B 站 Writeback 恒 false——P3 决议，不内置任何平台写回实现）。
/// </summary>
public sealed class PlatformLeaderboardTests
{
    private sealed class FakeAdapter : IPlatformLeaderboardAdapter
    {
        public string Platform { get; init; } = "douyin";

        public bool TrySubmit(string scopeId, string participantId, long score) => true;
    }

    [Fact]
    public void Register_TryGet_ReturnsAdapter()
    {
        var registry = new PlatformLeaderboardRegistry();
        var adapter = new FakeAdapter();

        registry.Register(adapter);

        Assert.True(registry.Supports("douyin"));
        Assert.Same(adapter, registry.TryGet("douyin"));
    }

    [Fact]
    public void TryGet_UnregisteredPlatform_ReturnsNull()
    {
        var registry = new PlatformLeaderboardRegistry();

        // 未注册平台 fail-closed：调用方拿到 null 必须显式决定降级，不猜默认行为。
        Assert.Null(registry.TryGet("kuaishou"));
        Assert.False(registry.Supports("kuaishou"));
    }

    [Fact]
    public void PlatformName_IsCaseInsensitive()
    {
        var registry = new PlatformLeaderboardRegistry();
        registry.Register(new FakeAdapter { Platform = "douyin" });

        Assert.Same(registry.TryGet("douyin"), registry.TryGet("DouYin"));
    }

    [Fact]
    public void Register_SamePlatformTwice_ReplacesAdapter()
    {
        var registry = new PlatformLeaderboardRegistry();
        var first = new FakeAdapter();
        var second = new FakeAdapter();

        registry.Register(first);
        registry.Register(second);

        Assert.Same(second, registry.TryGet("douyin"));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void TrySubmit_UnregisteredPlatform_FailsVisible()
    {
        var registry = new PlatformLeaderboardRegistry();

        Assert.False(registry.TrySubmit("kuaishou", "room-1", "p-1", 10));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void TrySubmit_BlankPlatform_FailsVisible(string? platform)
    {
        var registry = new PlatformLeaderboardRegistry();

        Assert.False(registry.TrySubmit(platform!, "room-1", "p-1", 10));
    }

    [Fact]
    public void TrySubmit_BlankScopeOrParticipant_Throws()
    {
        var registry = new PlatformLeaderboardRegistry();
        registry.Register(new FakeAdapter());

        // 作用域/参与者空值是契约违规：fail-visible 抛错，不静默丢写（§9 RankValidate 段）。
        Assert.ThrowsAny<ArgumentException>(() => registry.TrySubmit("douyin", " ", "p-1", 10));
        Assert.ThrowsAny<ArgumentException>(() => registry.TrySubmit("douyin", "room-1", " ", 10));
    }

    [Fact]
    public void Register_BlankPlatform_Throws()
    {
        var registry = new PlatformLeaderboardRegistry();

        Assert.ThrowsAny<ArgumentException>(() => registry.Register(new FakeAdapter { Platform = " " }));
    }
}
