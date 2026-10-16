using System.Diagnostics;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// SessionActor 生命周期守卫与连接替换：空 SessionId 拒绝、Start 幂等、
/// 同名客户端重连替换（旧连接立即停收，新连接照常收）。
/// </summary>
public sealed class SessionActorLifecycleTests
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static SessionConfig Config() => new()
    {
        TickDurationMs = 20,
        SnapshotEveryTicks = 1,
        MaxLagTicks = 100_000,
    };

    [Fact]
    public void EmptySessionId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new SessionActor("", Config(), new CountingGameModule()));
    }

    [Fact]
    public async Task Start_Twice_KeepsSingleLoop()
    {
        await using var actor = new SessionActor("s-life-start", Config(), new CountingGameModule());

        actor.Start();
        actor.Start();

        // 单循环仍在正常推进 tick（第二个 Start 被幂等挡掉）。
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && actor.Diagnostics.TickId < 2)
        {
            await Task.Delay(10);
        }

        Assert.True(actor.Diagnostics.TickId >= 2, $"ticks={actor.Diagnostics.TickId}");
    }

    [Fact]
    public async Task DisposeAsync_RacingCallers_OnlyOneRunsShutdown()
    {
        for (var iteration = 0; iteration < 25; iteration++)
        {
            var actor = new SessionActor($"s-life-dispose-{iteration}", Config(), new CountingGameModule());
            actor.Start();
            await Task.Delay(5);

            var barrier = new Barrier(8);
            var disposals = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await actor.DisposeAsync();
            })).ToArray();

            await Task.WhenAll(disposals);
        }
    }

    [Fact]
    public async Task ReAddClient_ReplacesPreviousConnection()
    {
        var oldReceived = new List<SessionSnapshot>();
        var newReceived = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-life-readd", Config(), new CountingGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", oldReceived));
        actor.Start();

        await UntilAsync(() => Count(oldReceived) >= 3);

        // 同名重连：新连接替换旧连接。
        await actor.AddClientAsync(new CapturingClient("c1", newReceived));
        actor.Start(); // 幂等：actor 已在跑，重复 Start 无效但无害。

        await UntilAsync(() => Count(newReceived) >= 3);

        // 旧连接的出站槽已被替换掉，不可能再收到任何快照。
        var frozen = Count(oldReceived);
        await Task.Delay(300);
        Assert.Equal(frozen, Count(oldReceived));
        Assert.Single(actor.Diagnostics.Clients);
    }

    private static int Count(List<SessionSnapshot> snapshots)
    {
        Lock.Wait();
        try
        {
            return snapshots.Count;
        }
        finally
        {
            Lock.Release();
        }
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate(), "timed out waiting for the actor");
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
        public string Name => "counting-lifecycle";

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
