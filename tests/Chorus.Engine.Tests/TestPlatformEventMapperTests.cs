using Chorus.Adapters.Platforms;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

public sealed class TestPlatformEventMapperTests
{
    [Fact]
    public void Mapper_MapsGiftWithMatchAndFactionIds()
    {
        var mapper = new TestPlatformEventMapper();
        const string raw = """
            {
              "sessionId": "s1",
              "eventId": "e1",
              "userId": "u1",
              "kind": "Gift",
              "matchId": "m1",
              "factionId": "blue"
            }
            """;

        var ev = mapper.TryMap(PlatformNames.Test, raw, ingestTimeMs: 1234);

        Assert.NotNull(ev);
        Assert.Equal("m1", ev!.MatchId);
        Assert.Equal("blue", ev.FactionId);
        Assert.Equal(AudienceEventKind.Gift, ev.Kind);
        Assert.Equal(PlatformNames.Test, ev.Platform);
    }

    [Fact]
    public void Mapper_MapsComment()
    {
        var mapper = new TestPlatformEventMapper();
        const string raw = """
            {
              "sessionId": "s1",
              "eventId": "e1",
              "userId": "u1",
              "kind": "Comment",
              "matchId": "m1",
              "factionId": "blue"
            }
            """;

        var ev = mapper.TryMap(PlatformNames.Test, raw, ingestTimeMs: 1234);

        Assert.NotNull(ev);
        Assert.Equal("m1", ev!.MatchId);
        Assert.Equal("blue", ev.FactionId);
        Assert.Equal(AudienceEventKind.Comment, ev.Kind);
    }

    [Fact]
    public void Mapper_IgnoresOtherPlatforms()
    {
        var mapper = new TestPlatformEventMapper();

        Assert.Null(mapper.TryMap(PlatformNames.Douyin, """{"sessionId":"s1"}""", ingestTimeMs: 1));
    }

    [Fact]
    public void Mapper_MissingSessionId_ReturnsNull()
    {
        var mapper = new TestPlatformEventMapper();

        Assert.Null(mapper.TryMap(PlatformNames.Test, """{"eventId":"e1"}""", ingestTimeMs: 1));
    }

    [Fact]
    public void Mapper_BlankSessionId_ReturnsNull()
    {
        var mapper = new TestPlatformEventMapper();

        Assert.Null(mapper.TryMap(PlatformNames.Test, """{"sessionId":"   ","eventId":"e1"}""", ingestTimeMs: 1));
    }

    [Fact]
    public void Mapper_NumericKind_ParsedAsEnum()
    {
        var mapper = new TestPlatformEventMapper();
        const string raw = """{"sessionId":"s1","eventId":"e1","userId":"u1","kind":3}""";

        var ev = mapper.TryMap(PlatformNames.Test, raw, ingestTimeMs: 1);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Gift, ev!.Kind);
    }

    [Fact]
    public void Mapper_UnknownStringKind_FallsBackToComment()
    {
        var mapper = new TestPlatformEventMapper();
        const string raw = """{"sessionId":"s1","eventId":"e1","userId":"u1","kind":"UnknownKind"}""";

        var ev = mapper.TryMap(PlatformNames.Test, raw, ingestTimeMs: 1);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Comment, ev!.Kind);
    }
}
