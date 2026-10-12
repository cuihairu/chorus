using System.Text;
using Chorus.Gateway.Core.Faces;

namespace Chorus.Engine.Tests;

public sealed class IngressFaceGuardTests
{
    [Fact]
    public void RateLimiter_AllowsUpToLimit_ThenRejects()
    {
        var limiter = new IngressRateLimiter(limitPerWindow: 3, windowMs: 60_000);
        var now = 1_000_000L;

        Assert.True(limiter.Allow("ip1", now));
        Assert.True(limiter.Allow("ip1", now));
        Assert.True(limiter.Allow("ip1", now));
        Assert.False(limiter.Allow("ip1", now));
    }

    [Fact]
    public void RateLimiter_NewWindow_ResetsCount()
    {
        var limiter = new IngressRateLimiter(limitPerWindow: 1, windowMs: 60_000);
        var now = 1_000_000L;

        Assert.True(limiter.Allow("ip1", now));
        Assert.False(limiter.Allow("ip1", now + 30_000));
        Assert.True(limiter.Allow("ip1", now + 61_000));
    }

    [Fact]
    public void RateLimiter_DifferentSources_IndependentWindows()
    {
        var limiter = new IngressRateLimiter(limitPerWindow: 1, windowMs: 60_000);
        var now = 1_000_000L;

        Assert.True(limiter.Allow("ip1", now));
        Assert.True(limiter.Allow("ip2", now));
        Assert.False(limiter.Allow("ip1", now));
    }

    [Fact]
    public void RateLimiter_BlankSource_TreatedAsUnknownKey()
    {
        var limiter = new IngressRateLimiter(limitPerWindow: 1, windowMs: 60_000);
        var now = 1_000_000L;

        Assert.True(limiter.Allow("", now));
        Assert.False(limiter.Allow("", now));
        Assert.True(limiter.Allow("ip1", now));
    }

    [Fact]
    public async Task ReadBodyAsync_WithinCap_ReturnsBody()
    {
        var guard = new IngressFaceGuard(new IngressFaceOptions { MaxBodyBytes = 1024 });
        var payload = Encoding.UTF8.GetBytes("""{"event":"comment"}""");

        var result = await guard.ReadBodyAsync(new MemoryStream(payload), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(result.TooLarge);
        Assert.Equal("""{"event":"comment"}""", result.Body);
    }

    [Fact]
    public async Task ReadBodyAsync_OverCap_TooLarge()
    {
        var guard = new IngressFaceGuard(new IngressFaceOptions { MaxBodyBytes = 16 });
        var payload = Encoding.UTF8.GetBytes(new string('x', 64));

        var result = await guard.ReadBodyAsync(new MemoryStream(payload), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.TooLarge);
        Assert.Null(result.Body);
    }

    [Fact]
    public void CreateRequestCancellationToken_TimesOutWithinBudget()
    {
        var guard = new IngressFaceGuard(new IngressFaceOptions { TimeoutMs = 50 });

        using var cts = guard.CreateRequestCancellationToken(CancellationToken.None);

        // WaitOne returns true when the token was cancelled, false on its own timeout.
        Assert.True(cts.Token.WaitHandle.WaitOne(5_000), "face timeout token should cancel within the budget");
    }
}
