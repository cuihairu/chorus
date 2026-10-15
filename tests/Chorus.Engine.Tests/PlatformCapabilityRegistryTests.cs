using Chorus.Adapters.Platforms;
using Chorus.Engine.Routing;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// 平台能力注册金测（todo §14 平台能力注册项，契约 = 总体架构审计 §17.3）：
/// 注册表与已落地映射器能力对齐（B 站四类型、抖音/快手占位三类型），
/// 未注册平台 fail-closed，B 站反向写入恒 false（P3 明确不做）。
/// </summary>
public sealed class PlatformCapabilityRegistryTests
{
    private static PlatformCapabilityRegistry NewRegistry()
        => new();

    [Fact]
    public void Register_ThenSupportsEventKind()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);

        Assert.True(registry.SupportsEventKind(PlatformNames.Bilibili, AudienceEventKind.Comment));
        Assert.True(registry.SupportsEventKind(PlatformNames.Bilibili, AudienceEventKind.Gift));
        Assert.True(registry.SupportsEventKind(PlatformNames.Bilibili, AudienceEventKind.Enter));
        Assert.True(registry.SupportsEventKind(PlatformNames.Bilibili, AudienceEventKind.RoomLifecycle));
    }

    [Fact]
    public void SupportsEventKind_UnregisteredKind_False()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);

        // B 站映射器不产 Like（open-live 无对应事件）。
        Assert.False(registry.SupportsEventKind(PlatformNames.Bilibili, AudienceEventKind.Like));
    }

    [Fact]
    public void SupportsEventKind_UnknownPlatform_False()
    {
        var registry = NewRegistry();

        // fail-closed：未注册平台一律不支持，不猜测。
        Assert.False(registry.SupportsEventKind("some-future-platform", AudienceEventKind.Comment));
    }

    [Fact]
    public void SupportsEventKind_Unknown_Kind_False()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);

        Assert.False(registry.SupportsEventKind(PlatformNames.Bilibili, AudienceEventKind.Unknown));
    }

    [Fact]
    public void SupportsWriteback_Bilibili_False()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);

        // B 站 P3 反向写入明确不做（无官方通道）。
        Assert.False(registry.SupportsWriteback(PlatformNames.Bilibili));
    }

    [Fact]
    public void SupportsWriteback_WithWritebackFlag_True()
    {
        var registry = NewRegistry();
        var caps = new PlatformCapabilities(
            new HashSet<AudienceEventKind> { AudienceEventKind.Comment },
            PlatformCapabilityFlags.Ingest | PlatformCapabilityFlags.Writeback);
        registry.Register("future-platform", caps);

        Assert.True(registry.SupportsWriteback("future-platform"));
    }

    [Fact]
    public void Register_ReplacesPreviousCapabilities()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Douyin, PlatformCapabilities.Douyin);
        registry.Register(PlatformNames.Douyin, new PlatformCapabilities(
            new HashSet<AudienceEventKind> { AudienceEventKind.Comment }, PlatformCapabilityFlags.Ingest));

        Assert.True(registry.SupportsEventKind(PlatformNames.Douyin, AudienceEventKind.Comment));
        Assert.False(registry.SupportsEventKind(PlatformNames.Douyin, AudienceEventKind.Gift));
    }

    [Fact]
    public void Platform_NameCaseInsensitive()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Douyin, PlatformCapabilities.Douyin);

        Assert.True(registry.SupportsEventKind("DOUYIN", AudienceEventKind.Gift));
    }

    [Fact]
    public void Count_TracksRegistrations()
    {
        var registry = NewRegistry();
        Assert.Equal(0, registry.Count);

        registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);
        Assert.Equal(1, registry.Count);

        registry.Register(PlatformNames.Douyin, PlatformCapabilities.Douyin);
        Assert.Equal(2, registry.Count);

        // 幂等整行替换不增加计数。
        registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void TryGet_Registered_ReturnsSameCapabilities()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Douyin, PlatformCapabilities.Douyin);

        Assert.True(registry.TryGet(PlatformNames.Douyin, out var caps));
        Assert.Same(PlatformCapabilities.Douyin, caps);

        // 平台名大小写不敏感，与 Register 一致。
        Assert.True(registry.TryGet("DOUYIN", out var capsUpper));
        Assert.Same(PlatformCapabilities.Douyin, capsUpper);
    }

    [Fact]
    public void TryGet_Unregistered_ReturnsFalse()
    {
        var registry = NewRegistry();

        Assert.False(registry.TryGet("some-future-platform", out var caps));
        Assert.Null(caps);
    }

    [Fact]
    public void Snapshot_ListsRegisteredPlatforms()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);
        registry.Register(PlatformNames.Douyin, PlatformCapabilities.Douyin);

        var snapshot = registry.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, c => c.Key == PlatformNames.Bilibili);
        Assert.Contains(snapshot, c => c.Key == PlatformNames.Douyin);
    }

    [Fact]
    public void BilibiliDefaults_AlignWithMapperCapabilities()
    {
        // 注册表默认值必须与 BilibiliPlatformEventMapper 实际产出的类型对齐：
        // 映射器产出的每个类型都在注册能力内，未归一化类型（Unknown）不在内。
        var registry = NewRegistry();
        registry.Register(PlatformNames.Bilibili, PlatformCapabilities.Bilibili);
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");

        var samples = new (string Json, AudienceEventKind Expected)[]
        {
            ("""{"cmd":"DANMU_MSG","msg_id":"m1","data":{"open_id":"ou","msg":"hi"}}""", AudienceEventKind.Comment),
            ("""{"cmd":"SEND_GIFT","msg_id":"g1","data":{"open_id":"ou","gift_id":1,"gift_num":1,"price":1}}""", AudienceEventKind.Gift),
            ("""{"cmd":"SEND_GIFT_V2","msg_id":"g2","data":{"open_id":"ou"}}""", AudienceEventKind.Gift),
            ("""{"cmd":"INTERACT_WORD_V2","msg_id":"e1","data":{"open_id":"ou"}}""", AudienceEventKind.Enter),
            ("""{"cmd":"LIVE_START","msg_id":"l1","data":{}}""", AudienceEventKind.RoomLifecycle),
        };

        foreach (var (json, expected) in samples)
        {
            var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);
            Assert.NotNull(ev);
            Assert.Equal(expected, ev!.Kind);
            Assert.True(
                registry.SupportsEventKind(PlatformNames.Bilibili, ev.Kind),
                $"mapper produces {ev.Kind} but registry does not list it");
        }

        var unmapped = mapper.TryMap(PlatformNames.Bilibili,
            """{"cmd":"SUPER_CHAT_MESSAGE","msg_id":"sc1","data":{"open_id":"ou"}}""", 0);
        Assert.NotNull(unmapped);
        Assert.Equal(AudienceEventKind.Unknown, unmapped!.Kind);
        Assert.False(registry.SupportsEventKind(PlatformNames.Bilibili, unmapped.Kind));
    }

    [Fact]
    public void DouyinDefaults_PlaceholderContract()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Douyin, PlatformCapabilities.Douyin);

        // 抖音占位契约（非官方 schema，见 DouyinPlatformEventMapper 头注）。
        Assert.True(registry.SupportsEventKind(PlatformNames.Douyin, AudienceEventKind.Comment));
        Assert.True(registry.SupportsEventKind(PlatformNames.Douyin, AudienceEventKind.Gift));
        Assert.True(registry.SupportsEventKind(PlatformNames.Douyin, AudienceEventKind.Like));
        Assert.False(registry.SupportsEventKind(PlatformNames.Douyin, AudienceEventKind.Enter));
    }

    [Fact]
    public void KuaishouDefaults_PlaceholderContract()
    {
        var registry = NewRegistry();
        registry.Register(PlatformNames.Kuaishou, PlatformCapabilities.Kuaishou);

        Assert.True(registry.SupportsEventKind(PlatformNames.Kuaishou, AudienceEventKind.Comment));
        Assert.True(registry.SupportsEventKind(PlatformNames.Kuaishou, AudienceEventKind.Gift));
        Assert.True(registry.SupportsEventKind(PlatformNames.Kuaishou, AudienceEventKind.Like));
    }
}
