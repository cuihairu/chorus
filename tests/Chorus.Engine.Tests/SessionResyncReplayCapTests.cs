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

        await UntilAsync(() => SnapshotMaxTick(harness.Received) >= 3);
        var fromFrameId = SnapshotMaxTick(harness.Received) - 2;
        await harness.Actor.ResyncAsync("c1", fromFrameId);

        // Only the replay can deliver an already-broadcast tick again.
        await UntilAsync(() => LockRun(harness, r => r.Any(s => s.TickId == fromFrameId)));
        await UntilAsync(() => harness.Actor.Diagnostics.TotalResyncReplays == 1);
        Assert.Equal(0, harness.Actor.Diagnostics.TotalResyncReplayCapped);
    }

    [Fact]
    public async Task GapBeyondCap_AnswersOneForcedSnapshot()
    {
        await using var harness = new Harness(maxResyncReplayTicks: 4);
        await harness.StartWithClientAsync();

        await UntilAsync(() => SnapshotMaxTick(harness.Received) >= 8);
        long tickAtRequest;
        int countAtRequest;
        Lock.Wait();
        try
        {
            tickAtRequest = MaxTickLocked(harness.Received);
            countAtRequest = harness.Received.Count;
        }
        finally
        {
            Lock.Release();
        }

        // Gap of 8 retained frames > cap of 4: no replay burst of old frames,
        // one directed forced snapshot instead.
        var fromFrameId = tickAtRequest - 7;
        await harness.Actor.ResyncAsync("c1", fromFrameId);

        await UntilAsync(() => LockRun(harness, r => r.Any(s => s.Forced == true && s.Reason == "resync-gap" && s.TargetClientId == "c1")));
        await UntilAsync(() => harness.Actor.Diagnostics.TotalResyncReplayCapped == 1);

        Assert.Equal(0, harness.Actor.Diagnostics.TotalResyncReplays);
        // Nothing at or before the request-time tick arrived after the request:
        // the capped answer must not smuggle a partial replay (new ticks only).
        int oldFramesAfter;
        Lock.Wait();
        try
        {
            oldFramesAfter = harness.Received.Skip(countAtRequest).Count(s => s.TickId <= tickAtRequest);
        }
        finally
        {
            Lock.Release();
        }

        Assert.Equal(1, oldFramesAfter); // the forced answer itself, nothing else
    }

    [Fact]
    public async Task CapZero_DisablesReplay_AlwaysForcedSnapshot()
    {
        await using var harness = new Harness(maxResyncReplayTicks: 0);
        await harness.StartWithClientAsync();

        await UntilAsync(() => SnapshotMaxTick(harness.Received) >= 3);
        await harness.Actor.ResyncAsync("c1", SnapshotMaxTick(harness.Received) - 1);

        await UntilAsync(() => harness.Actor.Diagnostics.TotalResyncReplayCapped == 1);
        Assert.Equal(0, harness.Actor.Diagnostics.TotalResyncReplays);
        await UntilAsync(() => LockRun(harness, r => r.Any(s => s.Forced == true && s.Reason == "resync-gap")));
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

    private static async Task UntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException("expected condition never arrived");
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
