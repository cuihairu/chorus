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
}
