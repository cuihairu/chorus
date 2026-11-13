using Chorus.Adapters.Platforms;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

public sealed class DouyinPlatformMapperTests
{
    private const long IngestTimeMs = 1234;

    [Fact]
    public void TryMap_Comment_MapsAllFields()
    {
        var mapper = new DouyinPlatformEventMapper();
        const string raw = """
            {
              "event": "comment",
              "event_id": "dy-1",
              "session_id": "s1",
              "user_id": "u1",
              "content": "hello",
              "match_id": "m1",
              "faction_id": "red"
            }
            """;

        var ev = mapper.TryMap(PlatformNames.Douyin, raw, IngestTimeMs);

        Assert.NotNull(ev);
        Assert.Equal("dy-1", ev!.EventId);
        Assert.Equal(PlatformNames.Douyin, ev.Platform);
        Assert.Equal("s1", ev.SessionId);
        Assert.Equal("u1", ev.UserId);
        Assert.Equal(AudienceEventKind.Comment, ev.Kind);
        Assert.Equal("hello", ev.Text);
        Assert.Equal("m1", ev.MatchId);
        Assert.Equal("red", ev.FactionId);
        Assert.Equal(IngestTimeMs, ev.IngestTimeMs);
    }

    [Fact]
    public void TryMap_Gift_MapsGiftPayload()
    {
        var mapper = new DouyinPlatformEventMapper();
        const string raw = """
            {
              "event": "gift",
              "event_id": "dy-2",
              "session_id": "s1",
              "user_id": "u1",
              "gift_id": "g1",
              "gift_count": 3,
              "gift_value": 10
            }
            """;

        var ev = mapper.TryMap(PlatformNames.Douyin, raw, IngestTimeMs);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Gift, ev!.Kind);
        Assert.Equal("g1", ev.GiftId);
        Assert.Equal(3, ev.GiftCount);
        Assert.Equal(10, ev.GiftValue);
    }

    [Fact]
    public void TryMap_Like_MapsLikeKind()
    {
        var mapper = new DouyinPlatformEventMapper();
        const string raw = """
            {"event": "like", "event_id": "dy-3", "session_id": "s1", "user_id": "u1"}
            """;

        var ev = mapper.TryMap(PlatformNames.Douyin, raw, IngestTimeMs);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Like, ev!.Kind);
    }

    [Fact]
    public void TryMap_UnknownEventType_DoesNotCrash_MapsToUnknownKind()
    {
        var mapper = new DouyinPlatformEventMapper();
        const string raw = """
            {"event": "follow", "event_id": "dy-4", "session_id": "s1", "user_id": "u1"}
            """;

        var ev = mapper.TryMap(PlatformNames.Douyin, raw, IngestTimeMs);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Unknown, ev!.Kind);
    }

    [Fact]
    public void TryMap_MissingEventType_MapsToUnknownKind()
    {
        var mapper = new DouyinPlatformEventMapper();
        const string raw = """
            {"event_id": "dy-5", "session_id": "s1"}
            """;

        var ev = mapper.TryMap(PlatformNames.Douyin, raw, IngestTimeMs);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Unknown, ev!.Kind);
        Assert.Equal("anonymous", ev.UserId);
    }

    [Fact]
    public void TryMap_MalformedJson_ReturnsNull()
    {
        var mapper = new DouyinPlatformEventMapper();

        Assert.Null(mapper.TryMap(PlatformNames.Douyin, "not json", IngestTimeMs));
    }

    [Fact]
    public void TryMap_MissingSessionId_ReturnsNull()
    {
        var mapper = new DouyinPlatformEventMapper();

        Assert.Null(mapper.TryMap(PlatformNames.Douyin, """{"event":"comment","event_id":"dy-6"}""", IngestTimeMs));
    }

    [Fact]
    public void TryMap_MissingEventId_IsRefused()
    {
        var mapper = new DouyinPlatformEventMapper();
        const string raw = """{"event":"comment","session_id":"s1"}""";

        // 无来源 ID 即拒绝映射（端点答 400 unmapped_event），不再随机兜底：
        // 随机 id 会让同一报文的每次重试都绕过去重。
        Assert.Null(mapper.TryMap(PlatformNames.Douyin, raw, IngestTimeMs));
        Assert.Null(mapper.TryMap(PlatformNames.Douyin, """{"event":"comment","event_id":"","session_id":"s1"}""", IngestTimeMs));
    }

    [Fact]
    public void TryMap_SamePayloadRetry_KeepsSourceIdentity()
    {
        var mapper = new DouyinPlatformEventMapper();
        const string raw = """{"event":"comment","event_id":"dy-retry","session_id":"s1"}""";

        // 相同报文重试必须保持来源标识不变，下游去重才能应答重放。
        var first = mapper.TryMap(PlatformNames.Douyin, raw, IngestTimeMs);
        var retry = mapper.TryMap(PlatformNames.Douyin, raw, IngestTimeMs + 100);

        Assert.NotNull(first);
        Assert.NotNull(retry);
        Assert.Equal(first!.EventId, retry!.EventId);
    }

    [Fact]
    public void TryMap_OtherPlatform_ReturnsNull()
    {
        var mapper = new DouyinPlatformEventMapper();
        const string raw = """{"event":"comment","session_id":"s1"}""";

        Assert.Null(mapper.TryMap(PlatformNames.Kuaishou, raw, IngestTimeMs));
        Assert.Null(mapper.TryMap(PlatformNames.Test, raw, IngestTimeMs));
    }
}
