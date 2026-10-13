using Chorus.Adapters.Platforms;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// B站 open-live → AudienceEvent 归一化金测（P1 弹幕）：DANMU_MSG → comment
/// （open_id/msg/msg_id 按 blivedm models/open_live.py，✅ 原文核实）；
/// 未知 cmd 不崩（Unknown，管线确认后丢弃）；无会话绑定 fail-visible。
/// </summary>
public sealed class BilibiliPlatformMapperTests
{
    private const string DanmuJson =
        """{"cmd":"DANMU_MSG","msg_id":"m-1","data":{"open_id":"ou-777","uname":"viewer","msg":"666"}}""";

    [Fact]
    public void DanmuMsg_MapsToComment()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");

        var ev = mapper.TryMap(PlatformNames.Bilibili, DanmuJson, ingestTimeMs: 1234);

        Assert.NotNull(ev);
        Assert.Equal(PlatformNames.Bilibili, ev.Platform);
        Assert.Equal("s-bili", ev.SessionId);
        Assert.Equal(AudienceEventKind.Comment, ev.Kind);
        Assert.Equal("ou-777", ev.UserId);
        Assert.Equal("666", ev.Text);
        Assert.Equal("m-1", ev.EventId);
        Assert.Equal(1234, ev.IngestTimeMs);
    }

    [Fact]
    public void DanmuMsg_MissingOpenId_FallsBackToAnonymous()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string json = """{"cmd":"DANMU_MSG","msg_id":"m-2","data":{"uname":"viewer","msg":"hi"}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal("anonymous", ev.UserId);
    }

    [Fact]
    public void DanmuMsg_MissingMsgId_SynthesizesEventId()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string json = """{"cmd":"DANMU_MSG","data":{"open_id":"u","msg":"hi"}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.StartsWith("bilibili:", ev.EventId);
    }

    [Fact]
    public void UnknownCmd_MapsToUnknownKind_DoesNotCrash()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        // SEND_GIFT 属 P2；灰度期未知类型（如 SEND_GIFT_V2）也不崩。
        const string json = """{"cmd":"SEND_GIFT_V2","msg_id":"m-3","data":{}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Unknown, ev.Kind);
        Assert.Equal("m-3", ev.EventId);
    }

    [Fact]
    public void WrongPlatform_ReturnsNull()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");

        Assert.Null(mapper.TryMap(PlatformNames.Douyin, DanmuJson, 0));
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");

        Assert.Null(mapper.TryMap(PlatformNames.Bilibili, "not-json", 0));
    }

    [Fact]
    public void NoSessionBinding_ReturnsNull()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: null);

        Assert.Null(mapper.TryMap(PlatformNames.Bilibili, DanmuJson, 0));
    }
}
