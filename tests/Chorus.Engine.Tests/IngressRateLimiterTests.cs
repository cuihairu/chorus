using Chorus.Gateway.Core.Faces;

namespace Chorus.Engine.Tests;

/// <summary>
/// Gateway 入口固定窗口限流：窗口滚动、per-window 上限、以及表满时的
/// 清扫（只移除闲置窗口，活跃窗口与限额行为不受影响）。
/// </summary>
public sealed class IngressRateLimiterTests
{
    [Fact]
    public void Window_RollsOver_AndLimitsPerWindow()
    {
        var limiter = new IngressRateLimiter(limitPerWindow: 1, windowMs: 100);

        Assert.True(limiter.Allow("ip-1", nowMs: 0));
        Assert.False(limiter.Allow("ip-1", nowMs: 10));
        // 窗口滚动后重新计数。
        Assert.True(limiter.Allow("ip-1", nowMs: 100));
    }

    [Fact]
    public void Constructor_ClampsLimitToAtLeastOne()
    {
        var limiter = new IngressRateLimiter(limitPerWindow: 0, windowMs: 100);

        Assert.True(limiter.Allow("ip-1", nowMs: 0));
        Assert.False(limiter.Allow("ip-1", nowMs: 1));
    }

    [Fact]
    public void Sweep_RemovesIdleWindows_KeepsLive()
    {
        var limiter = new IngressRateLimiter(limitPerWindow: 1, windowMs: 100);

        // 一个早已过期的窗口 + 一个活跃窗口，再把表灌到 10k 触发清扫。
        Assert.True(limiter.Allow("stale", nowMs: 0));
        Assert.True(limiter.Allow("live", nowMs: 5_000));
        for (var i = 0; i < 9_998; i++)
        {
            Assert.True(limiter.Allow($"f{i}", nowMs: 5_000));
        }

        // 第 10001 个键触发 SweepExpired：stale（闲置两个窗口）被移除。
        Assert.True(limiter.Allow("newkey", nowMs: 5_000));
        // 活跃窗口在清扫后仍然生效——这是清扫不误伤的行为证据。
        Assert.False(limiter.Allow("live", nowMs: 5_050));
    }

    [Fact]
    public void EmptySourceKey_FallsBackToUnknown()
    {
        var limiter = new IngressRateLimiter(limitPerWindow: 1, windowMs: 100);

        Assert.True(limiter.Allow("", nowMs: 0));
        // "" 与 null 都落到同一个 "unknown" 桶。
        Assert.False(limiter.Allow("", nowMs: 1));
        Assert.False(limiter.Allow(null!, nowMs: 2));
    }
}
