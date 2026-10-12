using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

public sealed class KeywordGiftAudienceFactionRuleTests
{
    [Fact]
    public void Apply_MapsFactionFromCommentKeyword()
    {
        var rule = new KeywordGiftAudienceFactionRule(
            commentKeywords: new Dictionary<string, string>
            {
                ["#red"] = "red",
            });

        var ev = new AudienceEvent(
            EventId: "e1",
            Platform: "test",
            SessionId: "s1",
            UserId: "u1",
            Kind: AudienceEventKind.Comment,
            IngestTimeMs: 1000,
            Text: "  #RED ");

        var mapped = rule.Apply(ev);

        Assert.Equal("red", mapped.FactionId);
    }

    [Fact]
    public void Apply_MapsFactionFromGiftId()
    {
        var rule = new KeywordGiftAudienceFactionRule(
            giftIds: new Dictionary<string, string>
            {
                ["gift_blue"] = "blue",
            });

        var ev = new AudienceEvent(
            EventId: "e1",
            Platform: "test",
            SessionId: "s1",
            UserId: "u1",
            Kind: AudienceEventKind.Gift,
            IngestTimeMs: 1000,
            GiftId: "GIFT_BLUE");

        var mapped = rule.Apply(ev);

        Assert.Equal("blue", mapped.FactionId);
    }

    [Fact]
    public void Apply_PreservesExistingFactionId()
    {
        var rule = new KeywordGiftAudienceFactionRule(
            commentKeywords: new Dictionary<string, string>
            {
                ["#red"] = "red",
            });

        var ev = new AudienceEvent(
            EventId: "e1",
            Platform: "test",
            SessionId: "s1",
            UserId: "u1",
            Kind: AudienceEventKind.Comment,
            IngestTimeMs: 1000,
            Text: "#red",
            FactionId: "blue");

        var mapped = rule.Apply(ev);

        Assert.Equal("blue", mapped.FactionId);
    }
}
