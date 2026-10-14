using System.Diagnostics;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-3 acceptance (快照恢复): a client that stops ACKing falls
/// behind MaxLagTicks and receives a forced resync snapshot; once it ACKs the
/// current tick, periodic snapshots go back to non-forced.
/// </summary>
public sealed class SessionActorResyncTests
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    [Fact]
    public async Task NoAck_TriggersForcedResync_ThenAckClearsLag()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-resync", new SessionConfig
        {
            TickDurationMs = 20,
            SnapshotEveryTicks = 1,
            MaxLagTicks = 3,
            ResyncCooldownMs = 50,
        }, new CountingGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        // Phase 1: no ACKs at all -> lag grows past MaxLagTicks -> forced resync.
        await AnyAsync(received, s => s.Forced == true);
        Assert.True(actor.Diagnostics.TotalResyncSnapshotsSent >= 1);
        SessionSnapshot forced;
        long latest;
        int countAfterAck;
        Lock.Wait();
        try
        {
            forced = received.First(s => s.Forced == true && s.Reason == "lag");
            latest = received.Max(s => s.TickId);
            countAfterAck = received.Count;
        }
        finally
        {
            Lock.Release();
        }

        Assert.Equal("lag", forced.Reason);

        // Phase 2: the client catches up -> snapshots are no longer forced.
        await actor.AckAsync("c1", latest, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await AnyAsync(received, s => s.Forced == false, startIndex: countAfterAck);
    }

    [Fact]
    public async Task ForcedResync_FiresOffPeriodicTicks()
    {
        // SnapshotEveryTicks=0：没有任何周期快照可搭车，滞后客户端的强制
        // 重同步必须在非周期 tick 上自行发出——且每条都是定向 Forced 包。
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-resync-offperiod", new SessionConfig
        {
            TickDurationMs = 20,
            SnapshotEveryTicks = 0,
            MaxLagTicks = 3,
            ResyncCooldownMs = 50,
        }, new CountingGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        await AnyAsync(received, s =>
            s.Forced == true && s.Reason == "lag" && s.TargetClientId == "c1");

        Lock.Wait();
        try
        {
            // 从未有广播快照混入：全部是定向重同步。
            Assert.All(received, s => Assert.Equal("c1", s.TargetClientId));
        }
        finally
        {
            Lock.Release();
        }

        Assert.True(actor.Diagnostics.TotalResyncSnapshotsSent >= 1);
    }

    private static async Task AnyAsync(List<SessionSnapshot> snapshots, Func<SessionSnapshot, bool> predicate, int startIndex = 0)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            await Lock.WaitAsync();
            try
            {
                var copy = snapshots.Skip(startIndex).ToList();
                if (copy.Any(predicate))
                {
                    return;
                }
            }
            finally
            {
                Lock.Release();
            }

            if (sw.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException("expected snapshot never arrived");
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
        public string Name => "counting-resync";

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
