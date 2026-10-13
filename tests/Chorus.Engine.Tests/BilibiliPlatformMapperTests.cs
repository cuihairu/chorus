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
        // 平台协议仍在演进：真正未登记的 cmd（如未来新增类型）不崩、确认后丢弃。
        const string json = """{"cmd":"SOME_FUTURE_EVENT","msg_id":"m-3","data":{}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Unknown, ev.Kind);
        Assert.Equal("m-3", ev.EventId);
    }

    [Fact]
    public void SendGift_MapsToGift()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string json =
            """{"cmd":"SEND_GIFT","msg_id":"g-1","data":{"open_id":"ou-7","gift_id":31036,"gift_num":2,"price":1000,"gift_name":"小花花"}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Gift, ev.Kind);
        Assert.Equal("31036", ev.GiftId);
        Assert.Equal(2, ev.GiftCount);
        Assert.Equal(1000, ev.GiftValue);
        Assert.Equal("ou-7", ev.UserId);
    }

    [Fact]
    public void SendGiftV2_GrayFormat_Tolerated_NotLostNotCrashing()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        // 灰度 V2：字段布局未定/部分缺失 → 仍是 Gift，不崩、不丢（去重靠 msg_id）。
        const string json = """{"cmd":"SEND_GIFT_V2","msg_id":"g-v2","data":{"open_id":"ou-8"}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Gift, ev.Kind);
        Assert.Equal("g-v2", ev.EventId);
        Assert.Null(ev.GiftId);
        Assert.Null(ev.GiftCount);
    }

    [Fact]
    public void InteractWordV2_And_LiveRoomEnter_MapToEnter()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");

        var interact = mapper.TryMap(PlatformNames.Bilibili,
            """{"cmd":"INTERACT_WORD_V2","msg_id":"e-1","data":{"open_id":"ou-9"}}""", 0);
        var enter = mapper.TryMap(PlatformNames.Bilibili,
            """{"cmd":"LIVE_ROOM_ENTER","msg_id":"e-2","data":{"open_id":"ou-10"}}""", 0);

        Assert.NotNull(interact);
        Assert.Equal(AudienceEventKind.Enter, interact.Kind);
        Assert.Equal("ou-9", interact.UserId);
        Assert.NotNull(enter);
        Assert.Equal(AudienceEventKind.Enter, enter.Kind);
    }

    [Fact]
    public void LiveStart_And_LiveEnd_MapToRoomLifecycle()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");

        var start = mapper.TryMap(PlatformNames.Bilibili, """{"cmd":"LIVE_START","msg_id":"l-1","data":{}}""", 0);
        var end = mapper.TryMap(PlatformNames.Bilibili, """{"cmd":"LIVE_END","msg_id":"l-2","data":{}}""", 0);

        Assert.NotNull(start);
        Assert.Equal(AudienceEventKind.RoomLifecycle, start.Kind);
        Assert.Equal("live_start", start.Text);
        Assert.NotNull(end);
        Assert.Equal(AudienceEventKind.RoomLifecycle, end.Kind);
        Assert.Equal("live_end", end.Text);
    }

    [Fact]
    public void SuperChat_P2Pending_StaysUnknown()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string json = """{"cmd":"SUPER_CHAT_MESSAGE","msg_id":"sc-1","data":{"open_id":"ou-11"}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Unknown, ev.Kind);
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
