using System.Diagnostics;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review P0-5 acceptance: future-tick, regressing, and forged-identity ACKs
/// must never move the server's recorded sync progress; invalid/duplicate/
/// stale/accepted ACKs are classified into diagnostics counters.
///
/// Timing: the actor ticks live (10ms). Clients are added before any tick
/// (baseline LastAckTickId = 0); ACKs are only sent after the required ticks
/// have provably elapsed, and "future" uses a huge value so the rejection is
/// deterministic regardless of tick speed.
///
/// Ack application is gated on the counters themselves, not on TickId: the ack
/// sits in the control lane until the next lane drain, and observing TickId
/// advance does not prove the ack was classified yet, so under parallel test
/// load the asserts could read counters still mid-flight.
/// </summary>
public sealed class SessionActorAckTests
{
    private static SessionActor CreateActor()
    {
        var actor = new SessionActor("s-ack", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
        }, new CountingGameModule());
        return actor;
    }

    private static async Task WaitForTickAsync(SessionActor actor, long minTick, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (actor.Diagnostics.TickId < minTick)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"TickId {actor.Diagnostics.TickId} did not reach {minTick}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Waits until every ACK sent in the test has been classified into one of
    /// the four counters, then lets the caller assert the exact split.
    /// </summary>
    private static async Task WaitForAcksClassifiedAsync(SessionActor actor, int expected, int timeoutMs = 5000)
    {
        long Classified(SessionActor a)
        {
            var d = a.Diagnostics;
            return d.TotalAcksAccepted + d.TotalAcksInvalid + d.TotalAcksStale + d.TotalAcksDuplicate;
        }

        var sw = Stopwatch.StartNew();
        while (Classified(actor) < expected)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(
                    $"Only {Classified(actor)}/{expected} acks classified after {timeoutMs}ms");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task FutureTickAck_Rejected_AsInvalid()
    {
        await using var actor = CreateActor();
        actor.Start();
        await actor.AddClientAsync(new NullClient("c1"));
        await WaitForTickAsync(actor, 1);

        await actor.AckAsync("c1", lastAppliedTickId: long.MaxValue - 1, clientTimeMs: 0);
        await WaitForAcksClassifiedAsync(actor, 1);

        Assert.Equal(0, actor.Diagnostics.TotalAcksAccepted);
        Assert.Equal(1, actor.Diagnostics.TotalAcksInvalid);
        Assert.All(actor.Diagnostics.Clients, c => Assert.NotEqual(long.MaxValue - 1, c.LastAckTickId));
    }

    [Fact]
    public async Task NegativeAck_Rejected_AsInvalid()
    {
        await using var actor = CreateActor();
        actor.Start();
        await actor.AddClientAsync(new NullClient("c1"));
        await WaitForTickAsync(actor, 1);

        await actor.AckAsync("c1", lastAppliedTickId: -1, clientTimeMs: 0);
        await WaitForAcksClassifiedAsync(actor, 1);

        Assert.Equal(1, actor.Diagnostics.TotalAcksInvalid);
        Assert.All(actor.Diagnostics.Clients, c => Assert.Equal(0, c.LastAckTickId));
    }

    [Fact]
    public async Task ForgedClientAck_Rejected_AsInvalid()
    {
        await using var actor = CreateActor();
        actor.Start();
        await actor.AddClientAsync(new NullClient("c1"));
        await WaitForTickAsync(actor, 1);

        // Unknown client id cannot move anyone's progress.
        await actor.AckAsync("not-registered", lastAppliedTickId: 1, clientTimeMs: 0);
        await WaitForAcksClassifiedAsync(actor, 1);

        Assert.Equal(1, actor.Diagnostics.TotalAcksInvalid);
        Assert.All(actor.Diagnostics.Clients, c => Assert.Equal(0, c.LastAckTickId));
    }

    [Fact]
    public async Task RegressingAck_Rejected_AsStale()
    {
        await using var actor = CreateActor();
        actor.Start();
        await actor.AddClientAsync(new NullClient("c1"));
        await WaitForTickAsync(actor, 1);

        await actor.AckAsync("c1", lastAppliedTickId: 1, clientTimeMs: 0);
        await actor.AckAsync("c1", lastAppliedTickId: 0, clientTimeMs: 0);
        await WaitForAcksClassifiedAsync(actor, 2);

        Assert.Equal(1, actor.Diagnostics.TotalAcksAccepted);
        Assert.Equal(1, actor.Diagnostics.TotalAcksStale);
        var client = actor.Diagnostics.Clients.Single(c => c.ClientId == "c1");
        Assert.Equal(1, client.LastAckTickId);
    }

    [Fact]
    public async Task DuplicateAck_Counted_NoStateChange()
    {
        await using var actor = CreateActor();
        actor.Start();
        await actor.AddClientAsync(new NullClient("c1"));
        await WaitForTickAsync(actor, 1);

        await actor.AckAsync("c1", lastAppliedTickId: 1, clientTimeMs: 0);
        await actor.AckAsync("c1", lastAppliedTickId: 1, clientTimeMs: 0);
        await WaitForAcksClassifiedAsync(actor, 2);

        Assert.Equal(1, actor.Diagnostics.TotalAcksAccepted);
        Assert.Equal(1, actor.Diagnostics.TotalAcksDuplicate);
        Assert.Equal(1, actor.Diagnostics.Clients.Single(c => c.ClientId == "c1").LastAckTickId);
    }

    [Fact]
    public async Task ProgressAcks_Accepted_PerClient()
    {
        await using var actor = CreateActor();
        actor.Start();
        await actor.AddClientAsync(new NullClient("c1"));
        await actor.AddClientAsync(new NullClient("c2"));
        await WaitForTickAsync(actor, 2);

        await actor.AckAsync("c1", lastAppliedTickId: 1, clientTimeMs: 0);
        await actor.AckAsync("c2", lastAppliedTickId: 2, clientTimeMs: 0);
        await WaitForAcksClassifiedAsync(actor, 2);

        Assert.Equal(2, actor.Diagnostics.TotalAcksAccepted);
        Assert.Equal(1, actor.Diagnostics.Clients.Single(c => c.ClientId == "c1").LastAckTickId);
        Assert.Equal(2, actor.Diagnostics.Clients.Single(c => c.ClientId == "c2").LastAckTickId);
    }

    private sealed class NullClient : ISessionClient
    {
        public NullClient(string clientId) => ClientId = clientId;

        public string ClientId { get; }

        public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CountingGameModule : IGameModule
    {
        public string Name => "counting-test";

        public object GetStateSnapshot() => new { ticks = 0 };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
        }

        public void Dispose()
        {
        }
    }
}
