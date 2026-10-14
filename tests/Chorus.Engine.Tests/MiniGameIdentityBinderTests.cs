using Chorus.Adapters.Platforms.MiniGame;
using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// 小游戏身份绑定金测（todo §17 身份映射）：登录 session → Chorus Player 身份
/// 确定性派生（重登幂等）、映射冲突 fail-visible 不隐式覆盖（防止被截取的登录
/// code 悄悄劫持别的玩家）、不可用 session fail-closed 返回 null。
/// </summary>
public sealed class MiniGameIdentityBinderTests
{
    private static MiniGameIdentityBinder NewBinder(
        PlatformIdentityRegistry? identities = null,
        Func<string, string, string>? factory = null)
        => new(identities ?? new PlatformIdentityRegistry(), factory);

    [Fact]
    public void TryBind_NewSession_BindsDeterministicPlayerIdentity()
    {
        var binder = NewBinder();

        var result = binder.TryBind(new MiniGameLoginSession("douyin", "open-1", "sk-1"));

        Assert.NotNull(result);
        Assert.True(result!.Mapped);
        Assert.Null(result.Conflicting);
        Assert.Equal(PlatformIdentityKind.Player, result.Identity.Kind);
        Assert.Equal("douyin:open-1", result.Identity.Id);
    }

    [Fact]
    public void TryBind_SameSessionTwice_IsIdempotent()
    {
        var binder = NewBinder();

        Assert.True(binder.TryBind(new MiniGameLoginSession("douyin", "open-1", "sk-1"))!.Mapped);
        Assert.True(binder.TryBind(new MiniGameLoginSession("douyin", "open-1", "sk-2"))!.Mapped);
    }

    [Fact]
    public void TryBind_DifferentPlatform_SameAccountId_AreDistinct()
    {
        var binder = NewBinder();

        Assert.Equal("douyin:open-1", binder.TryBind(new MiniGameLoginSession("douyin", "open-1"))!.Identity.Id);
        Assert.Equal("kuaishou:open-1", binder.TryBind(new MiniGameLoginSession("kuaishou", "open-1"))!.Identity.Id);
    }

    [Fact]
    public void TryBind_AccountAlreadyBoundToOtherIdentity_FailsVisible()
    {
        var identities = new PlatformIdentityRegistry();
        var defaultBinder = NewBinder(identities);
        defaultBinder.TryBind(new MiniGameLoginSession("douyin", "open-1"));

        // 同一平台账号经不同身份工厂映射 = 冲突：报告既有映射，不隐式覆盖，
        // 注册表保持原映射（防止被截取的登录 code 悄悄劫持别的玩家）。
        var result = NewBinder(identities, factory: (_, _) => "douyin:other")
            .TryBind(new MiniGameLoginSession("douyin", "open-1"))!;

        Assert.NotNull(result);
        Assert.False(result.Mapped);
        Assert.Equal("douyin:other", result.Identity.Id);          // 被拒的新映射
        Assert.Equal("douyin:open-1", result.Conflicting!.Id);    // 注册表里的既有映射
        Assert.Equal("douyin:open-1", defaultBinder.TryBind(new MiniGameLoginSession("douyin", "open-1"))!.Identity.Id);
    }

    [Fact]
    public void TryBind_CustomFactory_UsedForIdentityId()
    {
        var binder = NewBinder(factory: (platform, accountId) => $"p-{platform}-{accountId}");

        var result = binder.TryBind(new MiniGameLoginSession("douyin", "open-1"));

        Assert.NotNull(result);
        Assert.True(result!.Mapped);
        Assert.Equal("p-douyin-open-1", result.Identity.Id);
    }

    [Theory]
    [InlineData("", "open-1")]
    [InlineData("  ", "open-1")]
    [InlineData("douyin", null)]
    [InlineData("douyin", " ")]
    public void TryBind_BlankSessionParts_ReturnsNull(string? platform, string? accountId)
    {
        var binder = NewBinder();

        // 空平台名或空账号 id = 不可用 session：fail-closed 返回 null。
        Assert.Null(binder.TryBind(new MiniGameLoginSession(platform!, accountId!)));
    }

    [Fact]
    public void TryBind_NullSession_ReturnsNull()
    {
        Assert.Null(NewBinder().TryBind(null));
    }

    [Fact]
    public void TryBind_NullBinderDeps_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new MiniGameIdentityBinder(null!));
    }
}
