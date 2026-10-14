using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-2 acceptance (权威 Tick 去阻塞发送): a client whose transport
/// blocks must never stall the tick loop, and the queued snapshot is
/// latest-wins - on release the client receives the newest state, not a
/// backlog of stale snapshots.
/// </summary>
public sealed class SessionActorOutboundTests
{
    [Fact]
    public async Task SlowClient_TickKeepsAdvancing_LatestSnapshotWins()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-out", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 1,
        }, new CountingGameModule());

        await actor.AddClientAsync(new BlockingClient("slow-1", release.Task, snapshots));
        actor.Start();

        // The pump is stuck in the blocked send; ticks must keep advancing.
        await WaitForTickAsync(actor, 5);
        Assert.True(actor.Diagnostics.TickId >= 5);
        Assert.True(actor.Diagnostics.TotalSnapshotsSent >= 5);

        // Latest-wins proof: R marks the blocked window's end. Only snapshots
        // enqueued during the block (TickId <= R) can prove clobbering: the
        // old inline-send path would have delivered one per tick, but the
        // queue collapses them to at most two (in-flight + latest slot).
        var r = actor.Diagnostics.TickId;
        release.SetResult();

        await WaitForConditionAsync(() => actor.Diagnostics.TotalSnapshotsSent >= r + 3, timeoutMs: 5000);

        // Delivery must catch up past the blocked window. Compare against the
        // release point R, not the live server clock: ticks keep advancing
        // while the test thread schedules, so a clock comparison fails a
        // healthy system on a short stall.
        await WaitForConditionAsync(
            () =>
            {
                lock (snapshots)
                {
                    // First evaluation can race the pump's first post-release
                    // append - empty list just means not caught up yet.
                    return snapshots.Count > 0 && snapshots[^1].TickId >= r + 3;
                }
            },
            timeoutMs: 5000);

        lock (snapshots)
        {
            var fromBlockedWindow = snapshots.Count(s => s.TickId <= r);
            Assert.True(fromBlockedWindow <= 2,
                $"blocked window (ticks <= {r}) delivered {fromBlockedWindow} of {r} enqueued");
        }
    }

    [Fact]
    public async Task QueueDroppedFlowIntoSessionDiagnostics_AndPerClient()
    {
        // Audit B3 (frame-sync-best-practices §可观测性 "待导出"): the latest-wins
        // slot's DropOldest evictions are the single-client outbound backlog
        // signal and must surface in session diagnostics (per-client detail +
        // session total), then in the metrics render.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new SessionActor("s-drop", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 1,
        }, new CountingGameModule());

        await actor.AddClientAsync(new SlowClient(release.Task));
        actor.Start();

        try
        {
            // Pump enters the blocked first send (signal inside SendSnapshotAsync);
            // every further tick then overwrites the queued snapshot: 3 more ticks
            // guarantee at least one eviction without racing the blocked one.
            await WaitForConditionAsync(() => actor.Diagnostics.Clients.Count == 1, timeoutMs: 5000);
            await WaitForTickAsync(actor, 5);

            Assert.True(actor.Diagnostics.TotalClientQueueDropped >= 1,
                $"session total saw no drops ({actor.Diagnostics.TotalClientQueueDropped})");
            var client = Assert.Single(actor.Diagnostics.Clients);
            Assert.True(client.ClientQueueDropped >= 1,
                $"per-client drops saw no evictions ({client.ClientQueueDropped})");
        }
        finally
        {
            release.SetResult();
            await actor.DisposeAsync();
        }
    }

    private sealed class SlowClient : ISessionClient
    {
        private readonly Task _release;

        public SlowClient(Task release)
        {
            _release = release;
        }

        public string ClientId => "c-slow";

        public async ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            await _release.ConfigureAwait(false);
        }
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

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(10);
        }
    }

    private sealed class BlockingClient : ISessionClient
    {
        private readonly Task _release;
        private readonly List<SessionSnapshot> _snapshots;

        public BlockingClient(string clientId, Task release, List<SessionSnapshot> snapshots)
        {
            ClientId = clientId;
            _release = release;
            _snapshots = snapshots;
        }

        public string ClientId { get; }

        public async ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            await _release.ConfigureAwait(false);
            lock (_snapshots)
            {
                _snapshots.Add(snapshot);
            }
        }
    }

    private sealed class CountingGameModule : IGameModule
    {
        public string Name => "counting-out";

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
