using System.Diagnostics;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Frame-sync P2 acceptance (帧历史与补帧, todo §11): canonical snapshots enter
/// the frame history; a client-initiated resync inside the retained window
/// replays every frame in order over the direct-send path; a window whose
/// start was evicted (or history disabled) answers with the latest state plus
/// forced=true ("resync-gap"); repeats inside the per-client cooldown are
/// throttled; and applied events ride the snapshot's frameEvents envelope
/// stamped with the tick they were applied on.
/// </summary>
public sealed class SessionActorFrameHistoryTests
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static async Task WaitUntilTickAsync(SessionActor actor, long tick)
    {
        var sw = Stopwatch.StartNew();
        while (actor.Diagnostics.TickId < tick)
        {
            if (sw.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException($"tick {tick} never reached (at {actor.Diagnostics.TickId})");
            }
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException($"{what} never observed");
            }
            await Task.Delay(10);
        }
    }

    private static List<long> TickIds(List<SessionSnapshot> received)
    {
        Lock.Wait();
        try
        {
            return received.Select(s => s.TickId).ToList();
        }
        finally
        {
            Lock.Release();
        }
    }

    [Fact]
    public async Task ResyncWithinWindow_ReplaysEveryRetainedFrame()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-fill", new SessionConfig
        {
            TickDurationMs = 100,
            SnapshotEveryTicks = 1,
        }, new TinyGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        await WaitUntilTickAsync(actor, 5);
        await actor.ResyncAsync("c1", fromFrameId: 1);

        // Diagnostics are republished at the end of every tick, so poll for the
        // replay counter (bumped synchronously when the window hits) rather
        // than reading it the instant ResyncAsync returns.
        await WaitUntilAsync(
            () => actor.Diagnostics.TotalResyncReplays >= 1,
            "resync replay accepted");
        await WaitUntilAsync(() => TickIds(received).Contains(1), "replayed frame 1");

        var ticks = TickIds(received);
        Assert.Contains(1, ticks);
        Assert.Contains(2, ticks);
        Assert.Contains(3, ticks);
        Assert.True(actor.Diagnostics.TotalResyncRequests >= 1);
        Assert.True(actor.Diagnostics.TotalResyncReplays >= 1);
        Assert.Equal(0, actor.Diagnostics.TotalResyncWindowMisses);
    }

    [Fact]
    public async Task ResyncOutsideWindow_AnswersLatestForcedResyncGap()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-gap", new SessionConfig
        {
            TickDurationMs = 100,
            SnapshotEveryTicks = 1,
            FrameHistoryBytes = 0, // history disabled: every request must miss
        }, new TinyGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        await WaitUntilTickAsync(actor, 2);
        await actor.ResyncAsync("c1", fromFrameId: 1);

        await WaitUntilAsync(
            () => TickIds(received).Any(t => true) && SnapshotList(received).Any(s => s.Forced == true && s.Reason == "resync-gap"),
            "forced resync-gap answer");

        Lock.Wait();
        try
        {
            var gap = received.First(s => s.Forced == true && s.Reason == "resync-gap");
            Assert.Equal("c1", gap.TargetClientId);
            // Latest state AT RESYNC TIME, not at assertion time - the actor
            // keeps ticking while the replay lands on the client.
            Assert.InRange(gap.TickId, 1, actor.Diagnostics.TickId);
        }
        finally
        {
            Lock.Release();
        }

        await WaitUntilAsync(
            () => actor.Diagnostics.TotalResyncWindowMisses >= 1,
            "resync window miss counter");
        Assert.Equal(0, actor.Diagnostics.TotalResyncReplays);
    }

    [Fact]
    public async Task ResyncRepeat_InsideCooldown_IsThrottled()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-throttle", new SessionConfig
        {
            TickDurationMs = 100,
            SnapshotEveryTicks = 1,
            FrameHistoryBytes = 0,
            ResyncRequestCooldownMs = 60_000, // effectively everything after the first throttles
        }, new TinyGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        await WaitUntilTickAsync(actor, 2);
        await actor.ResyncAsync("c1", 1);
        await actor.ResyncAsync("c1", 1);
        await actor.ResyncAsync("c1", 1);

        await WaitUntilAsync(
            () => actor.Diagnostics.TotalResyncThrottled >= 2,
            "throttled resync requests");

        Assert.True(actor.Diagnostics.TotalResyncRequests >= 3);
    }

    [Fact]
    public async Task ResyncFromUnknownClient_IsCountedButIgnored()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-unknown", new SessionConfig
        {
            TickDurationMs = 100,
            SnapshotEveryTicks = 1,
        }, new TinyGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        await WaitUntilTickAsync(actor, 2);
        await actor.ResyncAsync("ghost", 1);

        await WaitUntilAsync(
            () => actor.Diagnostics.TotalResyncRequests >= 1,
            "resync request counter");

        Lock.Wait();
        try
        {
            Assert.DoesNotContain(received, s => s.Forced == true && s.Reason == "resync-gap");
        }
        finally
        {
            Lock.Release();
        }
        Assert.Equal(0, actor.Diagnostics.TotalResyncReplays);
        Assert.Equal(0, actor.Diagnostics.TotalResyncWindowMisses);
    }

    [Fact]
    public async Task AppliedEvents_RideFrameEventsEnvelope_WithAppliedTick()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-ev", new SessionConfig
        {
            TickDurationMs = 100,
            SnapshotEveryTicks = 1,
            InputDelayTicks = 1,
        }, new TinyGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        await actor.TryIngestAudienceEventAsync(new AudienceEvent(
            EventId: "ev-1",
            Platform: "test",
            SessionId: "s-ev",
            UserId: "u1",
            Kind: AudienceEventKind.Comment,
            IngestTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        // Prove the reducer applied the event first, then wait for the
        // envelope: the tick that applied it drains the accumulator into its
        // own snapshot, and slow ticks keep that snapshot off the latest-wins
        // drop path.
        await WaitUntilAsync(
            () => actor.Diagnostics.TotalAppliedEvents >= 1,
            "applied event");
        await WaitUntilAsync(
            () => SnapshotList(received).Any(s => s.FrameEvents != null),
            "snapshot with frameEvents");

        Lock.Wait();
        try
        {
            var withEvents = received.First(s => s.FrameEvents != null);
            var fe = withEvents.FrameEvents!;
            Assert.Single(fe);
            Assert.Equal("ev-1", fe[0].EventId);
            Assert.Equal("u1", fe[0].UserId);
            Assert.Equal("comment", fe[0].Kind);
            Assert.Equal(withEvents.TickId, fe[0].FrameId);
        }
        finally
        {
            Lock.Release();
        }
    }

    [Fact]
    public async Task SnapshotsWithoutEvents_OmitFrameEventsEnvelope()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-noev", new SessionConfig
        {
            TickDurationMs = 100,
            SnapshotEveryTicks = 1,
        }, new TinyGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        await WaitUntilTickAsync(actor, 3);

        Lock.Wait();
        try
        {
            Assert.True(received.Count >= 2);
            Assert.All(received, s => Assert.Null(s.FrameEvents));
        }
        finally
        {
            Lock.Release();
        }
    }

    private static List<SessionSnapshot> SnapshotList(List<SessionSnapshot> received)
    {
        Lock.Wait();
        try
        {
            return received.ToList();
        }
        finally
        {
            Lock.Release();
        }
    }

    private sealed class CapturingClient : ISessionClient
    {
        private readonly List<SessionSnapshot> _received;

        public CapturingClient(string clientId, List<SessionSnapshot> received)
        {
            ClientId = clientId;
            _received = received;
        }

        public string ClientId { get; }

        public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            Lock.Wait();
            try
            {
                _received.Add(snapshot);
            }
            finally
            {
                Lock.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TinyGameModule : IGameModule
    {
        public string Name => "tiny-fill";
        public object GetStateSnapshot() => new { counter = 1 };
        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events) { }
        public void Dispose() { }
    }
}
