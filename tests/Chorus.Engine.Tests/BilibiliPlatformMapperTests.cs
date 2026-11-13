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
    public void DanmuMsg_MissingMsgId_IsRefused()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");

        // 无 msg_id 即拒绝映射（端点答 400 unmapped_event），不再随机兜底：
        // 随机 id 会让同一报文的每次重试都绕过所有下游来源去重。
        Assert.Null(mapper.TryMap(PlatformNames.Bilibili, """{"cmd":"DANMU_MSG","data":{"open_id":"u","msg":"hi"}}""", 0));
        Assert.Null(mapper.TryMap(PlatformNames.Bilibili, """{"cmd":"DANMU_MSG","msg_id":"","data":{"open_id":"u","msg":"hi"}}""", 0));
    }

    [Fact]
    public void DanmuMsg_SamePayloadRetry_KeepsSourceIdentity()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string json = """{"cmd":"DANMU_MSG","msg_id":"m-retry","data":{"open_id":"u","msg":"hi"}}""";

        // 相同报文重试必须保持来源标识不变，下游去重才能应答重放。
        var first = mapper.TryMap(PlatformNames.Bilibili, json, 0);
        var retry = mapper.TryMap(PlatformNames.Bilibili, json, 100);

        Assert.NotNull(first);
        Assert.NotNull(retry);
        Assert.Equal(first!.EventId, retry!.EventId);
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
    public void SuperChat_MapsToSuperChatWithPriceAndMessage()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string json =
            """{"cmd":"SUPER_CHAT_MESSAGE","msg_id":"sc-1","data":{"open_id":"ou-11","message":"上车！","price":50}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.SuperChat, ev.Kind);
        Assert.Equal("ou-11", ev.UserId);
        Assert.Equal("上车！", ev.Text);
        Assert.Equal(50, ev.GiftValue);
        Assert.Equal("sc-1", ev.EventId);
    }

    [Fact]
    public void SuperChat_WebDanmakuFieldFallsBackToMsg()
    {
        // 留言文本字段名未获官方物料锁定：open-live 载荷 message 与网页弹幕形态 msg 都取，
        // 二者同时在场时以 open-live 字段优先。
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string json =
            """{"cmd":"SUPER_CHAT_MESSAGE","msg_id":"sc-2","data":{"open_id":"ou-11","msg":"兜底文案","message":"优先文案","price":30}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal("优先文案", ev.Text);
    }

    [Fact]
    public void SuperChat_MissingTextAndPrice_MapsKindWithNulls()
    {
        // 双字段回退：只有 msg 也取到。
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string fallbackOnly =
            """{"cmd":"SUPER_CHAT_MESSAGE","msg_id":"sc-3","data":{"open_id":"ou-11","msg":"兜底文案"}}""";
        var ev = mapper.TryMap(PlatformNames.Bilibili, fallbackOnly, 0);
        Assert.NotNull(ev);
        Assert.Equal("兜底文案", ev.Text);

        // 留言与金额都缺失：类型仍成立（事件不丢、不崩），取值留空。
        const string bare = """{"cmd":"SUPER_CHAT_MESSAGE","msg_id":"sc-4","data":{"open_id":"ou-11"}}""";
        var ev2 = mapper.TryMap(PlatformNames.Bilibili, bare, 0);
        Assert.NotNull(ev2);
        Assert.Equal(AudienceEventKind.SuperChat, ev2.Kind);
        Assert.Null(ev2.Text);
        Assert.Null(ev2.GiftValue);
    }

    [Fact]
    public void SuperChatDelete_StaysUnknown_DeletionNotRepresentable()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        // SC 撤销/退款语义在 AudienceEvent 里没有对应面：保持 Unknown，管线确认后丢弃。
        const string json = """{"cmd":"SUPER_CHAT_MESSAGE_DELETE","msg_id":"scd-1","data":{"ids":[1]}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Unknown, ev.Kind);
    }

    [Fact]
    public void GuardBuy_MapsToGuardBuyWithGuardLevelNumPrice()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        const string json =
            """{"cmd":"GUARD_BUY","msg_id":"gd-1","data":{"open_id":"ou-12","guard_level":3,"num":1,"price":198}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.GuardBuy, ev.Kind);
        Assert.Equal("ou-12", ev.UserId);
        Assert.Equal("3", ev.GiftId);
        Assert.Equal(1, ev.GiftCount);
        Assert.Equal(198, ev.GiftValue);
    }

    [Fact]
    public void UserToastMsgV2_MapsToGuardBuy_ToleratingMissingFields()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");
        // 上舰横幅与 GUARD_BUY 同义；V2 布局未定/字段缺失 → 仍 GuardBuy，不崩不丢（灰度容忍先例）。
        const string json = """{"cmd":"USER_TOAST_MSG_V2","msg_id":"ut-1","data":{"open_id":"ou-13"}}""";

        var ev = mapper.TryMap(PlatformNames.Bilibili, json, 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.GuardBuy, ev.Kind);
        Assert.Equal("ut-1", ev.EventId);
        Assert.Null(ev.GiftId);
        Assert.Null(ev.GiftCount);
        Assert.Null(ev.GiftValue);
    }

    [Fact]
    public void InteractionEnd_MapsToRoomLifecycle()
    {
        var mapper = new BilibiliPlatformEventMapper(sessionId: "s-bili");

        var ev = mapper.TryMap(PlatformNames.Bilibili,
            """{"cmd":"LIVE_OPEN_PLATFORM_INTERACTION_END","msg_id":"ie-1","data":{}}""", 0);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.RoomLifecycle, ev.Kind);
        Assert.Equal("interaction_end", ev.Text);
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
