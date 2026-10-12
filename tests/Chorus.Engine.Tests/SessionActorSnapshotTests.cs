using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-3 acceptance: snapshots carry a schema version, and states
/// exceeding MaxSnapshotBytes skip the send with a counter instead of pushing
/// oversize payloads at streamer clients (their resync loop takes over).
/// </summary>
public sealed class SessionActorSnapshotTests
{
    [Fact]
    public async Task Snapshot_CarriesSchemaVersion()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-ver", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 1,
        }, new CountingGameModule());

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            lock (received)
            {
                if (received.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        SessionSnapshot first;
        lock (received)
        {
            // The actor thread adds under this lock; read under it too.
            Assert.NotEmpty(received);
            first = received[0];
        }
        Assert.Equal(1, first.V);
        Assert.Equal("s-ver", first.SessionId);
    }

    [Fact]
    public async Task OversizeState_SendSkipped_Counted()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-big", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 1,
            MaxSnapshotBytes = 1024,
        }, new BigStateGameModule(payloadKilobytes: 64));

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        // Poll until the oversize skip is observed AND the actor has advanced
        // past the first tick (the first tick alone can satisfy the oversize
        // condition, so wait for tick >= 2 to prove survival before asserting).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((actor.Diagnostics.TotalSnapshotsOversize == 0 || actor.Diagnostics.TickId < 2)
               && sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
        }

        Assert.True(actor.Diagnostics.TotalSnapshotsOversize >= 1);
        Assert.Equal(0, actor.Diagnostics.TotalSnapshotsSent);
        lock (received)
        {
            Assert.Empty(received);
        }
        // The actor survives and keeps ticking.
        Assert.True(actor.Diagnostics.TickId >= 2);
    }

    [Fact]
    public async Task CapDisabled_NeverDrops()
    {
        var received = new List<SessionSnapshot>();
        await using var actor = new SessionActor("s-nocap", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 1,
            MaxSnapshotBytes = 0,
        }, new BigStateGameModule(payloadKilobytes: 64));

        await actor.AddClientAsync(new CapturingClient("c1", received));
        actor.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            lock (received)
            {
                if (received.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        lock (received)
        {
            // The actor thread adds under this lock; read under it too.
            Assert.NotEmpty(received);
        }
        Assert.Equal(0, actor.Diagnostics.TotalSnapshotsOversize);
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
            lock (_received)
            {
                _received.Add(snapshot);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingGameModule : IGameModule
    {
        public string Name => "counting-snap";

        public object GetStateSnapshot() => new { ticks = 0 };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class BigStateGameModule : IGameModule
    {
        private readonly string _payload;

        public BigStateGameModule(int payloadKilobytes) => _payload = new string('a', payloadKilobytes * 1024);

        public string Name => "big-state-snap";

        public object GetStateSnapshot() => new { blob = _payload };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
        }

        public void Dispose()
        {
        }
    }
}
