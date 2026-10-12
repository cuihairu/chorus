using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-2 acceptance: the bounded event lane back-pressures floods
/// (mergeable drops counted, gifts rejected as retryable Failed after the
/// bounded wait), a throwing GameModule never kills the actor, and tick
/// overruns are detected.
///
/// Some tests use an unstarted actor so the lane fills deterministically.
/// </summary>
public sealed class SessionActorBackpressureTests
{
    private static AudienceEvent Like(string id) => new(
        EventId: id, Platform: "test", SessionId: "s-bp", UserId: $"u-{id}",
        Kind: AudienceEventKind.Like, IngestTimeMs: 1000);

    private static AudienceEvent Gift(string id) => new(
        EventId: id, Platform: "test", SessionId: "s-bp", UserId: $"u-{id}",
        Kind: AudienceEventKind.Gift, IngestTimeMs: 1000, GiftId: "g1", GiftCount: 1, GiftValue: 1);

    [Fact]
    public async Task Flood_MergeableEvents_LaneRejectsOverflow()
    {
        await using var actor = new SessionActor("s-bp", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
            MaxPendingEvents = 4,
            EventEnqueueMaxWaitMs = 0,
        }, new CountingGameModule());

        // Unstarted: nobody drains the lane, capacity is deterministic.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await actor.TryIngestAudienceEventAsync(Like($"l{i}")));
        }

        Assert.False(await actor.TryIngestAudienceEventAsync(Like("overflow")));
        Assert.False(await actor.TryIngestAudienceEventAsync(Like("overflow2")));

        // Started: scheduled events apply, the actor is alive and healthy.
        actor.Start();
        await WaitAppliedAsync(actor, minApplied: 4);

        Assert.Equal(4, actor.Diagnostics.TotalAppliedEvents);
        Assert.Equal(2, actor.Diagnostics.TotalDroppedOverflow);
        Assert.Equal(0, actor.Diagnostics.TotalDroppedGifts);
        Assert.Equal(0, actor.Diagnostics.TotalTickErrors);
    }

    [Fact]
    public async Task Flood_Gift_WaitsBoundedThenRejectedAsFailed()
    {
        await using var actor = new SessionActor("s-bp", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
            MaxPendingEvents = 1,
            EventEnqueueMaxWaitMs = 200,
        }, new CountingGameModule());

        Assert.True(await actor.TryIngestAudienceEventAsync(Like("fill")));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var accepted = await actor.TryIngestAudienceEventAsync(Gift("g1"));
        sw.Stop();

        // Must-not-lose class: bounded wait (>= the configured wait, bounded by
        // it), then an explicit false the caller treats as Failed/retryable.
        Assert.False(accepted);
        Assert.True(sw.ElapsedMilliseconds >= 150, $"wait took {sw.ElapsedMilliseconds}ms");

        actor.Start();
        await WaitAppliedAsync(actor, minApplied: 1);
        Assert.Equal(1, actor.Diagnostics.TotalDroppedGifts);
    }

    [Fact]
    public async Task Flood_Gift_AcceptedWhenSpaceOpensDuringWait()
    {
        await using var actor = new SessionActor("s-bp", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
            MaxPendingEvents = 1,
            EventEnqueueMaxWaitMs = 2000,
        }, new CountingGameModule());

        Assert.True(await actor.TryIngestAudienceEventAsync(Like("fill")));

        actor.Start();

        // The actor drains the lane within a tick; the gift's bounded wait
        // should then find space and succeed.
        var accepted = await actor.TryIngestAudienceEventAsync(Gift("g1"));
        Assert.True(accepted);

        await WaitAppliedAsync(actor, minApplied: 2);
        Assert.Equal(0, actor.Diagnostics.TotalDroppedGifts);
    }

    /// <summary>Review stage-2: GameModule exceptions are isolated per tick.</summary>
    [Fact]
    public async Task GameModule_Throws_ActorSurvivesAndCountsErrors()
    {
        await using var actor = new SessionActor("s-throw", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
        }, new ThrowingGameModule());
        actor.Start();

        await WaitForTickAsync(actor, 3);

        Assert.True(actor.Diagnostics.TickId >= 3);
        Assert.True(actor.Diagnostics.TotalTickErrors >= 1);
        Assert.Equal(0, actor.Diagnostics.TotalAppliedEvents);
    }

    /// <summary>Review stage-2: ticks exceeding TickTimeoutMs are detected.</summary>
    [Fact]
    public async Task SlowTick_OverrunCounted()
    {
        await using var actor = new SessionActor("s-slow", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
            TickTimeoutMs = 5,
        }, new SlowGameModule(sleepMs: 40));
        actor.Start();

        // The applying tick sleeps 40ms against the 5ms threshold.
        Assert.True(await actor.TryIngestAudienceEventAsync(Like("slow-1")));
        await WaitAppliedAsync(actor, minApplied: 1);

        Assert.True(actor.Diagnostics.TotalTickOverruns >= 1);
        // The slow tick still completed and applied - detection, not cancellation.
        Assert.Equal(1, actor.Diagnostics.TotalAppliedEvents);
    }

    [Fact]
    public async Task MailboxDepth_DrainedAfterTick()
    {
        await using var actor = new SessionActor("s-depth", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
            MaxPendingEvents = 100,
        }, new CountingGameModule());

        for (var i = 0; i < 10; i++)
        {
            Assert.True(await actor.TryIngestAudienceEventAsync(Like($"l{i}")));
        }

        actor.Start();
        await WaitAppliedAsync(actor, minApplied: 10);

        Assert.Equal(0, actor.Diagnostics.MailboxDepth);
    }

    private static async Task WaitForTickAsync(SessionActor actor, long minTick, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (actor.Diagnostics.TickId < minTick)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"TickId {actor.Diagnostics.TickId} did not reach {minTick}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitAppliedAsync(SessionActor actor, long minApplied, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (actor.Diagnostics.TotalAppliedEvents < minApplied)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Applied {actor.Diagnostics.TotalAppliedEvents} did not reach {minApplied}");
            }

            await Task.Delay(10);
        }
    }

    private sealed class CountingGameModule : IGameModule
    {
        public string Name => "counting-bp";

        public object GetStateSnapshot() => new { ticks = 0 };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingGameModule : IGameModule
    {
        public string Name => "throwing-bp";

        public object GetStateSnapshot() => new { ticks = 0 };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
            => throw new InvalidOperationException("boom");

        public void Dispose()
        {
        }
    }

    private sealed class SlowGameModule : IGameModule
    {
        private readonly int _sleepMs;

        public SlowGameModule(int sleepMs) => _sleepMs = sleepMs;

        public string Name => "slow-bp";

        public object GetStateSnapshot() => new { ticks = 0 };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
            if (events.Count > 0)
            {
                Thread.Sleep(_sleepMs);
            }
        }

        public void Dispose()
        {
        }
    }
}
