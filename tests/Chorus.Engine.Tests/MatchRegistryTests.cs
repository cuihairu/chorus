using Chorus.Protocol.Models;
using Chorus.Gateway.Core.Faces;

namespace Chorus.Engine.Tests;

public sealed class MatchRegistryTests
{
    [Fact]
    public void Track_AggregatesSessionsAndFactionsPerMatch()
    {
        var registry = new MatchRegistry();

        registry.Track(new AudienceEvent(
            EventId: "e1",
            Platform: "test",
            SessionId: "s1",
            UserId: "u1",
            Kind: AudienceEventKind.Comment,
            IngestTimeMs: 1000,
            MatchId: "m1",
            FactionId: "red"));

        registry.Track(new AudienceEvent(
            EventId: "e2",
            Platform: "test",
            SessionId: "s2",
            UserId: "u2",
            Kind: AudienceEventKind.Gift,
            IngestTimeMs: 1001,
            MatchId: "m1",
            FactionId: "blue"));

        registry.Track(new AudienceEvent(
            EventId: "e3",
            Platform: "test",
            SessionId: "s2",
            UserId: "u3",
            Kind: AudienceEventKind.Like,
            IngestTimeMs: 1002,
            MatchId: "m1",
            FactionId: "blue"));

        var matches = registry.GetDiagnostics();

        var match = Assert.Single(matches);
        Assert.Equal("m1", match.Match.MatchId);
        Assert.Equal(new[] { "s1", "s2" }, match.Match.SessionIds);
        Assert.Equal(new[] { "blue", "red" }, match.Match.Factions.Select(x => x.FactionId).ToArray());
        Assert.Equal(new[] { "s2" }, match.Match.Factions.Single(x => x.FactionId == "blue").SessionIds);
        Assert.Equal(new[] { "s1" }, match.Match.Factions.Single(x => x.FactionId == "red").SessionIds);
        Assert.Equal(3, match.TotalEvents);
        Assert.Equal(1002, match.LastEventTimeMs);
    }

    [Fact]
    public void Track_IgnoresEventsWithoutMatchId()
    {
        var registry = new MatchRegistry();

        registry.Track(new AudienceEvent(
            EventId: "e1",
            Platform: "test",
            SessionId: "s1",
            UserId: "u1",
            Kind: AudienceEventKind.Comment,
            IngestTimeMs: 1000));

        Assert.Empty(registry.GetDiagnostics());
    }
}
