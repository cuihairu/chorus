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

    // Generous 15s ceilings: shared CI runners occasionally stall for several
    // seconds mid-test, and a green run only needs ~100ms per wait (one tick
    // drain). 5s ceilings flaked red on a loaded runner with the assertion
    // otherwise sound.
    private const int WaitCeilingMs = 15_000;

    private static async Task WaitUntilTickAsync(SessionActor actor, long tick)
    {
        var sw = Stopwatch.StartNew();
        while (actor.Diagnostics.TickId < tick)
        {
            if (sw.ElapsedMilliseconds > WaitCeilingMs)
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
            if (sw.ElapsedMilliseconds > WaitCeilingMs)
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

        // Per-client observability (the /sessions JSON surface): this client
        // joined at or before the first frame (it received the replay of
        // frame 1). ResyncCount/LastResyncAgeMs are lag-forced paths and are
        // pinned in SessionActorResyncTests.
        var client = Assert.Single(actor.Diagnostics.Clients);
        Assert.Equal("c1", client.ClientId);
        Assert.InRange(client.ConnectedTickId, 0, 1);
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
            ResyncRequestCooldownMs = 0, // drop-tolerant retry below, not the cooldown subject
        }, new TinyGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        await WaitUntilTickAsync(actor, 2);

        // The forced answer rides the same latest-wins outbound slot as the
        // regular broadcasts: a pump that lags past the next tick can drop it
        // behind a newer snapshot (state stays intact, only the message is
        // lost). The miss itself is deterministic with history disabled, so
        // re-request until one answer lands instead of waiting longer.
        SessionSnapshot? gap = null;
        var sw = Stopwatch.StartNew();
        while (gap is null && sw.ElapsedMilliseconds < WaitCeilingMs)
        {
            await actor.ResyncAsync("c1", fromFrameId: 1);
            await Task.Delay(100);
            Lock.Wait();
            try
            {
                gap = received.FirstOrDefault(s => s.Forced == true && s.Reason == "resync-gap");
            }
            finally
            {
                Lock.Release();
            }
        }

        Assert.NotNull(gap);
        Assert.Equal("c1", gap!.TargetClientId);
        // Latest state AT RESYNC TIME, not at assertion time - the actor
        // keeps ticking while the answer lands on the client.
        Assert.InRange(gap.TickId, 1, actor.Diagnostics.TickId);

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

        // The envelope rides the same latest-wins outbound slot as every
        // snapshot: a pump that lags past the next tick can drop the one
        // snapshot carrying the envelope behind the next (event-less) one,
        // and the drained accumulator is not re-emitted - the state itself
        // stays intact, only the envelope is lost. Waiting longer cannot fix
        // a drop, so re-ingest a fresh event per attempt: every applied
        // event re-enters the next drain, and some attempt must land.
        SessionSnapshot? carrier = null;
        var eventId = "";
        var attempts = 0;
        var sw = Stopwatch.StartNew();
        while (carrier is null && sw.ElapsedMilliseconds < WaitCeilingMs)
        {
            attempts++;
            eventId = $"ev-{attempts}";
            await actor.TryIngestAudienceEventAsync(new AudienceEvent(
                EventId: eventId,
                Platform: "test",
                SessionId: "s-ev",
                UserId: "u1",
                Kind: AudienceEventKind.Comment,
                IngestTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

            await WaitUntilAsync(
                () => actor.Diagnostics.TotalAppliedEvents >= attempts,
                $"applied event {eventId}");

            Lock.Wait();
            try
            {
                // One event in flight at a time, so the matching envelope
                // holds exactly this event (Assert.Single below).
                carrier = received.FirstOrDefault(s =>
                    s.FrameEvents != null && s.FrameEvents.Any(e => e.EventId == eventId));
            }
            finally
            {
                Lock.Release();
            }
        }

        Assert.NotNull(carrier);

        Lock.Wait();
        try
        {
            var fe = carrier!.FrameEvents!;
            Assert.Single(fe);
            Assert.Equal(eventId, fe[0].EventId);
            Assert.Equal("u1", fe[0].UserId);
            Assert.Equal("comment", fe[0].Kind);
            Assert.Equal(carrier!.TickId, fe[0].FrameId);
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

        // Ticks are counted on the actor, snapshots on the client: under load
        // the outbound pump can lag the tick loop, so poll for the deliveries
        // instead of assuming they landed the moment tick 3 published.
        await WaitUntilAsync(
            () => SnapshotList(received).Count >= 2,
            "first snapshots delivered");

        Lock.Wait();
        try
        {
            Assert.All(received, s => Assert.Null(s.FrameEvents));
        }
        finally
        {
            Lock.Release();
        }
    }

    [Fact]
    public async Task SingleTickOverAccumulatorCap_TrimsEnvelope_StateStaysIntact()
    {
        // 帧事件累计上限（SessionActor 私有 const 4096）：一整个批次超过上限
        // 时最老（此处即该批自己）被裁掉 —— 状态照常进快照、applied 计数不丢，
        // 只是信封不再携带这些帧事件；累计器永远有界。
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-trim", new SessionConfig
        {
            TickDurationMs = 500,
            SnapshotEveryTicks = 1,
            InputDelayTicks = 1,
            MaxEventsPerTick = 5000,
        }, new TinyGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        for (var i = 0; i < 4200; i++)
        {
            await actor.TryIngestAudienceEventAsync(new AudienceEvent(
                EventId: $"ev-{i}",
                Platform: "test",
                SessionId: "s-trim",
                UserId: "u1",
                Kind: AudienceEventKind.Comment,
                IngestTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        await WaitUntilAsync(
            () => actor.Diagnostics.TotalAppliedEvents >= 4200,
            "oversized batch never applied");
        await WaitUntilAsync(
            () => SnapshotList(received).Count > 0,
            "first snapshots delivered");

        var maxEnvelope = 0;
        Lock.Wait();
        try
        {
            maxEnvelope = received.Max(s => s.FrameEvents?.Count ?? 0);
        }
        finally
        {
            Lock.Release();
        }

        Assert.True(maxEnvelope <= 4096,
            $"frame envelope grew to {maxEnvelope}, above the accumulator cap");
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
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }

        public void Dispose() { }
    }
}
