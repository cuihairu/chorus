using System.Text.Json;
using Chorus.Protocol;
using Chorus.Protocol.Models;
using Chorus.SampleGame;

namespace Chorus.Engine.Tests;

public sealed class SampleGameModuleTests
{
    [Fact]
    public void GetStateSnapshot_IncludesMatchAndFactionBreakdown()
    {
        using var game = new SampleGameModule("s1");

        game.ApplyEvents(1, new[]
        {
            new AudienceEvent(
                EventId: "e1",
                Platform: "test",
                SessionId: "s1",
                UserId: "u1",
                Kind: AudienceEventKind.Comment,
                IngestTimeMs: 1000,
                MatchId: "m1",
                FactionId: "red"),
            new AudienceEvent(
                EventId: "e2",
                Platform: "test",
                SessionId: "s1",
                UserId: "u2",
                Kind: AudienceEventKind.Gift,
                IngestTimeMs: 1001,
                GiftCount: 3,
                MatchId: "m1",
                FactionId: "blue"),
            new AudienceEvent(
                EventId: "e3",
                Platform: "test",
                SessionId: "s1",
                UserId: "u1",
                Kind: AudienceEventKind.Like,
                IngestTimeMs: 1002,
                MatchId: "m1",
                FactionId: "red"),
        });

        var json = JsonSerializer.SerializeToElement(game.GetStateSnapshot(), ProtocolJson.SerializerOptions);

        Assert.Equal("m1", json.GetProperty("match").GetProperty("matchId").GetString());
        Assert.Equal(new[] { "s1" }, json.GetProperty("match").GetProperty("sessionIds").EnumerateArray().Select(x => x.GetString()).ToArray());

        var factions = json.GetProperty("match").GetProperty("factions").EnumerateArray().ToArray();
        Assert.Equal(2, factions.Length);

        var blue = factions.Single(f => f.GetProperty("factionId").GetString() == "blue");
        Assert.Equal(new[] { "s1" }, blue.GetProperty("sessionIds").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(1, blue.GetProperty("userCount").GetInt32());
        Assert.Equal(3, blue.GetProperty("gifts").GetInt32());

        var red = factions.Single(f => f.GetProperty("factionId").GetString() == "red");
        Assert.Equal(new[] { "s1" }, red.GetProperty("sessionIds").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(1, red.GetProperty("userCount").GetInt32());
        Assert.Equal(1, red.GetProperty("likes").GetInt32());
        Assert.Equal(1, red.GetProperty("comments").GetInt32());
    }

    [Fact]
    public void ModulesFromSameFactory_ShareMatchSnapshotAcrossSessions()
    {
        var factory = new SampleGameModuleFactory();
        using var game1 = (SampleGameModule)factory.Create("s1");
        using var game2 = (SampleGameModule)factory.Create("s2");

        game1.ApplyEvents(1, new[]
        {
            new AudienceEvent(
                EventId: "e1",
                Platform: "test",
                SessionId: "s1",
                UserId: "u1",
                Kind: AudienceEventKind.Comment,
                IngestTimeMs: 1000,
                MatchId: "m1",
                FactionId: "red"),
        });

        game2.ApplyEvents(1, new[]
        {
            new AudienceEvent(
                EventId: "e2",
                Platform: "test",
                SessionId: "s2",
                UserId: "u2",
                Kind: AudienceEventKind.Gift,
                IngestTimeMs: 1001,
                GiftCount: 2,
                MatchId: "m1",
                FactionId: "blue"),
        });

        var json = JsonSerializer.SerializeToElement(game1.GetStateSnapshot(), ProtocolJson.SerializerOptions);
        var match = json.GetProperty("match");

        Assert.Equal("m1", match.GetProperty("matchId").GetString());
        Assert.Equal(new[] { "s1", "s2" }, match.GetProperty("sessionIds").EnumerateArray().Select(x => x.GetString()).ToArray());

        var factions = match.GetProperty("factions").EnumerateArray().ToArray();
        Assert.Equal(2, factions.Length);
        var red = factions.Single(f => f.GetProperty("factionId").GetString() == "red");
        Assert.Equal(new[] { "s1" }, red.GetProperty("sessionIds").EnumerateArray().Select(x => x.GetString()).ToArray());
        var blue = factions.Single(f => f.GetProperty("factionId").GetString() == "blue");
        Assert.Equal(new[] { "s2" }, blue.GetProperty("sessionIds").EnumerateArray().Select(x => x.GetString()).ToArray());
    }
}
