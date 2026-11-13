using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

/// <summary>
/// 斗鱼骨架纪律金测：无官方技术 contract（docs/research/platforms/douyu-huya-bilibili-2026-10-30.md
/// 全列待确认），骨架面必须 fail closed——verifier 拒绝一切回调、mapper 一律无法映射、
/// 桥骨架不连接不转发。骨架预留 ≠ 支持。
/// </summary>
public sealed class DouyuSkeletonTests
{
    [Fact]
    public void Verifier_PlatformNameIsDouyu_AndRejectsEveryCallback()
    {
        var verifier = new DouyuPlatformEventVerifier();
        Assert.Equal(PlatformNames.Douyu, verifier.PlatformName);

        var headers = new Dictionary<string, string> { ["kwaisign"] = "whatever" };
        Assert.False(verifier.Verify(PlatformNames.Douyu, """{"cmd":"anything"}""", headers));
        // 重复调用同样拒绝（fail closed 是稳定行为，不是一次性警告路径）。
        Assert.False(verifier.Verify(PlatformNames.Douyu, """{"cmd":"anything"}""", headers));
    }

    [Fact]
    public void Verifier_RejectsOtherPlatformsToo()
    {
        var verifier = new DouyuPlatformEventVerifier();

        Assert.False(verifier.Verify(PlatformNames.Bilibili, "{}", new Dictionary<string, string>()));
        Assert.False(verifier.Verify(PlatformNames.Douyin, "{}", new Dictionary<string, string>()));
    }

    [Fact]
    public void Mapper_MapsNothing_NoFabricatedSchema()
    {
        var mapper = new DouyuPlatformEventMapper();

        Assert.Null(mapper.TryMap(PlatformNames.Douyu, """{"type":"chatmsg","content":"hi"}""", 0));
        Assert.Null(mapper.TryMap(PlatformNames.Bilibili, """{"cmd":"DANMU_MSG"}""", 0));
    }

    [Fact]
    public async Task BridgeSkeleton_StartsAndStopsWithoutConnectingAnything()
    {
        var bridge = new DouyuIngressBridgeSkeleton();

        await bridge.StartAsync(CancellationToken.None);
        await bridge.StartAsync(CancellationToken.None); // 幂等：重复 Start 不抛不连
        await bridge.StopAsync(CancellationToken.None);
    }
}
