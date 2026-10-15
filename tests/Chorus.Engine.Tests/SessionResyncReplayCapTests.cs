using System.Diagnostics;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Resync replay bounding (todo.md:156 churn blocker ③): a client-initiated
/// resync whose gap is larger than Session:MaxResyncReplayTicks must be
/// answered with ONE forced snapshot instead of replaying every retained
/// frame - each snapshot already carries full state, so the replay of an
/// oversized window only multiplies eng→gw volume (2026-10-15 gate run:
/// 27,568 replays × ~13 frames fed a 21.5k msg/s snapshot flood that froze
/// the receive loop). The cap bounds the per-request fan-out; the capped
/// outcome is its own counter, distinct from a history window miss.
/// </summary>
public sealed class SessionResyncReplayCapTests
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private sealed class Harness : IAsyncDisposable
    {
        public List<SessionSnapshot> Received { get; } = [];
        public SessionActor Actor { get; }

        public Harness(int maxResyncReplayTicks)
        {
            Actor = new SessionActor("s-replay-cap", new SessionConfig
            {
                TickDurationMs = 10,
                SnapshotEveryTicks = 1,
                // Disable the lag-forced path: the only Forced frames in these
                // tests must come from the resync-gap answer.
                MaxLagTicks = 0,
                ResyncRequestCooldownMs = 0,
                FrameHistoryBytes = 8 * 1024 * 1024,
                MaxResyncReplayTicks = maxResyncReplayTicks,
            }, new CountingGameModule());
        }

        public async Task StartWithClientAsync()
        {
            await Actor.AddClientAsync(new CapturingClient("c1", Received));
            Actor.Start();
        }

        public async ValueTask DisposeAsync() => await Actor.DisposeAsync();
    }

    [Fact]
    public async Task SmallGap_ReplaysFromHistory()
    {
        await using var harness = new Harness(maxResyncReplayTicks: 8);
        await harness.StartWithClientAsync();

        await UntilAsync(() => SnapshotSpan(harness.Received) >= 3);
        long minTick;
        int countBefore;
        Lock.Wait();
        try
        {
            minTick = MinTickLocked(harness.Received);
            countBefore = harness.Received.Count;
        }
        finally
        {
            Lock.Release();
        }

        // Small gap anchored on the delivered span (same reasoning as the gap
        // test below): [min+1 .. newest] is a retained window of 2+ frames,
        // within the cap of 8 - answered by replaying those frames.
        var fromFrameId = minTick + 1;
        await harness.Actor.ResyncAsync("c1", fromFrameId);

        // Only the replay can deliver the anchored tick again after the
        // request (broadcasts move strictly forward).
        await UntilAsync(() => LockRun(harness, r => r.Skip(countBefore).Any(s => s.TickId == fromFrameId)));
        await UntilAsync(() => harness.Actor.Diagnostics.TotalResyncReplays == 1);
        Assert.Equal(0, harness.Actor.Diagnostics.TotalResyncReplayCapped);
    }

    [Fact]
    public async Task GapBeyondCap_AnswersOneForcedSnapshot()
    {
        await using var harness = new Harness(maxResyncReplayTicks: 4);
        await harness.StartWithClientAsync();

        await UntilAsync(() => SnapshotSpan(harness.Received) >= 8);
        long tickAtRequest;
        long minTick;
        int countAtRequest;
        Lock.Wait();
        try
        {
            tickAtRequest = MaxTickLocked(harness.Received);
            minTick = MinTickLocked(harness.Received);
            countAtRequest = harness.Received.Count;
        }
        finally
        {
            Lock.Release();
        }

        // A gap of >= 8 retained frames > cap of 4: no replay burst of old
        // frames, one directed forced snapshot instead. The gap start anchors
        // on the oldest frame this client actually delivered - a "current - 7"
        // offset can predate the first stored frame (first broadcast lands a
        // tick or two after Start), which is a window MISS, not the capped
        // case this test exercises. Delivery also implies retention here: no
        // eviction can have touched it (byte cap >> this session's frames).
        var fromFrameId = minTick;
        // The forced answer travels the client's latest-wins slot like any
        // snapshot, so under a loaded run a newer broadcast can replace it
        // before the pump drains the slot (the queue's contract - the client
        // catches up on the next send). The cooldown is disabled here, so
        // keep asking until one lands instead of betting a single delivery
        // on scheduler timing.
        await UntilAsync(
            () =>
            {
                if (LockRun(harness, r => r.Any(s => s.Forced == true && s.Reason == "resync-gap" && s.TargetClientId == "c1")))
                {
                    return true;
                }

                _ = harness.Actor.ResyncAsync("c1", fromFrameId);
                return false;
            },
            () => Describe(harness));

        // Counters live on the actor's refreshed diagnostics snapshot, not on
        // the resync path - the Delivery above can beat the next refresh, so
        // wait for the numbers to land rather than reading the instant the
        // frame arrived.
        await UntilAsync(
            () =>
            {
                var d = harness.Actor.Diagnostics;
                return d.TotalResyncReplayCapped >= 1
                    && d.TotalResyncReplays == 0
                    // The window start is retained: capped, not a window miss.
                    && d.TotalResyncWindowMisses == 0;
            },
            () => Describe(harness));
        // No unforced frame at or before the request-time tick arrived after
        // the request: the capped answer must not smuggle a partial replay
        // (replays deliver over the direct ordered path, so a burst would
        // show up here regardless of slot timing).
        int unforcedOldFrames;
        Lock.Wait();
        try
        {
            unforcedOldFrames = harness.Received.Skip(countAtRequest).Count(s => s.Forced != true && s.TickId <= tickAtRequest);
        }
        finally
        {
            Lock.Release();
        }

        Assert.Equal(0, unforcedOldFrames);
    }

    [Fact]
    public async Task CapZero_DisablesReplay_AlwaysForcedSnapshot()
    {
        await using var harness = new Harness(maxResyncReplayTicks: 0);
        await harness.StartWithClientAsync();

        await UntilAsync(() => SnapshotMaxTick(harness.Received) >= 3);
        // Cap 0 = replay disabled outright: every request must take the forced
        // answer (MaxResyncReplayTicks > 0 is required even to attempt a
        // replay). The answer rides the client's latest-wins slot like any
        // snapshot and a busy run can replace it before the pump drains - keep
        // asking until one lands (same caveat as the gap test above).
        await UntilAsync(
            () =>
            {
                if (LockRun(harness, r => r.Any(s => s.Forced == true && s.Reason == "resync-gap")))
                {
                    return true;
                }

                _ = harness.Actor.ResyncAsync("c1", SnapshotMaxTick(harness.Received) - 1);
                return false;
            },
            () => Describe(harness));

        Assert.Equal(0, harness.Actor.Diagnostics.TotalResyncReplays);
    }

    [Fact]
    public void Renderer_ExposesReplayCappedCounter()
    {
        var diagnostics = new List<SessionDiagnostics>
        {
            new("s1", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<SessionClientDiagnostics>(),
                TotalResyncReplayCapped: 3),
            new("s2", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<SessionClientDiagnostics>(),
                TotalResyncReplayCapped: 4),
        };

        var engine = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(diagnostics, ipcPublishFailures: 0);
        Assert.Contains("# TYPE chorus_resync_replay_capped_total counter", engine);
        Assert.Contains("chorus_resync_replay_capped_total 7", engine);

        var server = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderServer(
            diagnostics,
            new Chorus.Gateway.Core.Faces.FaceCounters(),
            ipcPublishFailures: 0);
        Assert.Contains("chorus_resync_replay_capped_total 7", server);
    }

    private static long SnapshotMaxTick(List<SessionSnapshot> received)
    {
        Lock.Wait();
        try
        {
            return MaxTickLocked(received);
        }
        finally
        {
            Lock.Release();
        }
    }

    // Caller already holds Lock; SemaphoreSlim is not reentrant.
    private static long MaxTickLocked(List<SessionSnapshot> received)
        => received.Count == 0 ? -1 : received.Max(s => s.TickId);

    private static long MinTickLocked(List<SessionSnapshot> received)
        => received.Count == 0 ? -1 : received.Min(s => s.TickId);

    /// <summary>Distinct received-tick span (max - min + 1): waiting on this
    /// rather than max alone guarantees the anchored window start below is at
    /// least 8 frames from the newest retained frame - beyond the cap of 4 -
    /// even when a late first delivery shifts the whole span upward.</summary>
    private static long SnapshotSpan(List<SessionSnapshot> received)
    {
        Lock.Wait();
        try
        {
            return received.Count == 0 ? 0 : MaxTickLocked(received) - MinTickLocked(received) + 1;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>Timeout diagnostics: everything that could explain a wait that
    /// never satisfied (which counter fired, how much was delivered, how much
    /// the latest-wins slot dropped).</summary>
    private static string Describe(Harness harness)
    {
        var d = harness.Actor.Diagnostics;
        return $"tick={d.TickId} received={harness.Received.Count} maxTick={SnapshotMaxTick(harness.Received)} " +
               $"forcedSent={d.TotalResyncSnapshotsSent} slotDropped={d.TotalClientQueueDropped} " +
               $"resyncs={d.TotalResyncRequests} replays={d.TotalResyncReplays} capped={d.TotalResyncReplayCapped} " +
               $"misses={d.TotalResyncWindowMisses} throttled={d.TotalResyncThrottled} snapshots={d.TotalSnapshotsSent}";
    }

    private static T LockRun<T>(Harness harness, Func<List<SessionSnapshot>, T> read)
    {
        Lock.Wait();
        try
        {
            return read(harness.Received);
        }
        finally
        {
            Lock.Release();
        }
    }

    private static async Task UntilAsync(Func<bool> condition, Func<string>? describe = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000)
            {
                var detail = describe is null ? "" : $" state: {describe()}";
                throw new TimeoutException($"expected condition never arrived{detail}");
            }

            await Task.Delay(10);
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

    private sealed class CountingGameModule : IGameModule
    {
        public string Name => "replay-cap";

        public object GetStateSnapshot() => new { ticks = 0 };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
        }

        public void RestoreState(long version, System.Text.Json.JsonElement state)
        {
        }

        public void Dispose()
        {
        }
    }
}
