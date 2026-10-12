using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

public sealed class DefaultAudienceEventReducerTests
{
    [Fact]
    public void Reduce_OverLimit_MergesLikesAndGifts()
    {
        var reducer = new DefaultAudienceEventReducer();

        var events = new List<AudienceEvent>();
        for (var i = 0; i < 1000; i++)
        {
            events.Add(new AudienceEvent(
                EventId: $"e_like_{i}",
                Platform: "test",
                SessionId: "s1",
                UserId: $"u{i % 10}",
                Kind: AudienceEventKind.Like,
                IngestTimeMs: 1000 + i
            ));
        }
        for (var i = 0; i < 1000; i++)
        {
            events.Add(new AudienceEvent(
                EventId: $"e_gift_{i}",
                Platform: "test",
                SessionId: "s1",
                UserId: $"u{i % 10}",
                Kind: AudienceEventKind.Gift,
                IngestTimeMs: 2000 + i,
                GiftId: "g1",
                GiftCount: 1,
                GiftValue: 10
            ));
        }

        var reduced = reducer.Reduce(events, maxEventsPerTick: 50);

        Assert.True(reduced.Events.Count <= 50);
        Assert.Contains(reduced.Events, e => e.Kind == AudienceEventKind.Like && e.Platform == "merged");
        Assert.Contains(reduced.Events, e => e.Kind == AudienceEventKind.Gift && e.Platform == "merged");
        Assert.All(reduced.Events, e => Assert.Equal("s1", e.SessionId));
        Assert.True(reduced.MergedInputEvents > 0);
        Assert.Equal(0, reduced.DroppedInputEvents);
    }

    [Fact]
    public void Reduce_OverLimit_PreservesMatchAndFactionOnMergedEvents()
    {
        var reducer = new DefaultAudienceEventReducer();

        var events = new List<AudienceEvent>();
        for (var i = 0; i < 60; i++)
        {
            events.Add(new AudienceEvent(
                EventId: $"e_like_{i}",
                Platform: "test",
                SessionId: "s1",
                UserId: $"u{i % 3}",
                Kind: AudienceEventKind.Like,
                IngestTimeMs: 1000 + i,
                MatchId: "m1",
                FactionId: "red"
            ));
        }

        var reduced = reducer.Reduce(events, maxEventsPerTick: 5);

        Assert.NotEmpty(reduced.Events);
        Assert.All(reduced.Events, e =>
        {
            Assert.Equal("m1", e.MatchId);
            Assert.Equal("red", e.FactionId);
        });
    }

    /// <summary>
    /// Review P0-3 acceptance: one tick mixing red/blue likes and gifts must
    /// conserve per-faction counts across the merge, and no group may inherit
    /// another group's context (the old first-event bug).
    /// </summary>
    [Fact]
    public void Reduce_MixedFactions_ConservesPerFactionCounts()
    {
        var reducer = new DefaultAudienceEventReducer();

        // First event is blue: the old implementation leaked blue onto every merged event.
        AudienceEvent Like(string id, string user, string faction, long ts) => new(
            EventId: id, Platform: "test", SessionId: "s1", UserId: user,
            Kind: AudienceEventKind.Like, IngestTimeMs: ts, MatchId: "m1", FactionId: faction);
        AudienceEvent Gift(string id, string user, string faction, string gift, int count, int value, long ts) => new(
            EventId: id, Platform: "test", SessionId: "s1", UserId: user,
            Kind: AudienceEventKind.Gift, IngestTimeMs: ts, GiftId: gift,
            GiftCount: count, GiftValue: value, MatchId: "m1", FactionId: faction);

        var events = new List<AudienceEvent>
        {
            Like("l1", "u1", "blue", 100),
            Like("l2", "u1", "blue", 101),
            Like("l3", "u2", "blue", 102),
            Like("l4", "u1", "red", 103),
            Like("l5", "u1", "red", 104),
            Like("l6", "u2", "red", 105),
            Like("l7", "u3", "red", 106),
            Gift("g1", "u1", "red", "g1", 2, 1, 107),
            Gift("g2", "u1", "red", "g2", 1, 5, 108),
            Gift("g3", "u1", "blue", "g1", 1, 2, 109),
        };

        // 10 events over budget 6: likes merge to 2 groups (blue u1, blue u2, red u1..u3 = 5),
        // gifts merge to 2 groups (red u1, blue u1) -> total merged output 7... use 8.
        var reduced = reducer.Reduce(events, maxEventsPerTick: 8);

        Assert.Equal(0, reduced.DroppedInputEvents);

        var likes = reduced.Events.Where(e => e.Kind == AudienceEventKind.Like).ToList();
        var gifts = reduced.Events.Where(e => e.Kind == AudienceEventKind.Gift).ToList();

        // Like conservation per faction.
        Assert.Equal(3, likes.Where(e => e.FactionId == "blue").Sum(e => long.Parse(e.Text!)));
        Assert.Equal(4, likes.Where(e => e.FactionId == "red").Sum(e => long.Parse(e.Text!)));
        Assert.All(likes, e => Assert.Equal("s1", e.SessionId));

        // Gift conservation per faction and gift id.
        Assert.Equal(3, gifts.Where(e => e.FactionId == "red").Sum(e => e.GiftCount!.Value));
        Assert.Equal(1, gifts.Where(e => e.FactionId == "blue").Sum(e => e.GiftCount!.Value));

        // No context leak: blue group count must be exactly 2 (u1's two likes),
        // red 4 split across u1(2), u2(1), u3(1).
        var blueU1 = likes.Single(e => e.FactionId == "blue" && e.UserId == "u1");
        Assert.Equal(2, long.Parse(blueU1.Text!));
        var redU3 = likes.Single(e => e.FactionId == "red" && e.UserId == "u3");
        Assert.Equal(1, long.Parse(redU3.Text!));

        // Gift totals keep value aggregation: red g1 (count 2, value 1 each) = 2.
        var redG1 = gifts.Single(e => e.FactionId == "red" && e.GiftId == "g1");
        Assert.Equal(2, redG1.GiftCount);
        Assert.Equal(2, redG1.GiftValue);
        var redG2 = gifts.Single(e => e.FactionId == "red" && e.GiftId == "g2");
        Assert.Equal(1, redG2.GiftCount);
        Assert.Equal(5, redG2.GiftValue);
        var blueG1 = gifts.Single(e => e.FactionId == "blue" && e.GiftId == "g1");
        Assert.Equal(1, blueG1.GiftCount);
        Assert.Equal(2, blueG1.GiftValue);
    }

    [Fact]
    public void Reduce_SameUserDifferentFaction_NotMerged()
    {
        var reducer = new DefaultAudienceEventReducer();

        var events = new List<AudienceEvent>();
        for (var i = 0; i < 10; i++)
        {
            events.Add(new AudienceEvent(
                EventId: $"e_{i}",
                Platform: "test",
                SessionId: "s1",
                UserId: "u1",
                Kind: AudienceEventKind.Like,
                IngestTimeMs: 1000 + i,
                MatchId: "m1",
                FactionId: i % 2 == 0 ? "red" : "blue"
            ));
        }

        var reduced = reducer.Reduce(events, maxEventsPerTick: 2);

        var likes = reduced.Events.Where(e => e.Kind == AudienceEventKind.Like).ToList();
        Assert.Equal(2, likes.Count);
        Assert.Equal(5, likes.Where(e => e.FactionId == "red").Sum(e => long.Parse(e.Text!)));
        Assert.Equal(5, likes.Where(e => e.FactionId == "blue").Sum(e => long.Parse(e.Text!)));
    }

    [Fact]
    public void Reduce_SameUserDifferentSession_NotMerged()
    {
        var reducer = new DefaultAudienceEventReducer();

        var events = new List<AudienceEvent>();
        for (var i = 0; i < 10; i++)
        {
            events.Add(new AudienceEvent(
                EventId: $"e_{i}",
                Platform: "test",
                SessionId: i % 2 == 0 ? "s1" : "s2",
                UserId: "u1",
                Kind: AudienceEventKind.Like,
                IngestTimeMs: 1000 + i
            ));
        }

        var reduced = reducer.Reduce(events, maxEventsPerTick: 2);

        var likes = reduced.Events.Where(e => e.Kind == AudienceEventKind.Like).ToList();
        Assert.Equal(2, likes.Count);
        Assert.All(likes, e => Assert.Null(e.MatchId));
        Assert.Equal(5, likes.Where(e => e.SessionId == "s1").Sum(e => long.Parse(e.Text!)));
        Assert.Equal(5, likes.Where(e => e.SessionId == "s2").Sum(e => long.Parse(e.Text!)));
    }
}
