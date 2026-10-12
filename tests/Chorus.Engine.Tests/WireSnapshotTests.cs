using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Acceptance for the snapshot serialize-once reuse (todo §8 P3 快照序列化):
/// the broadcast path encodes the wire snapshot once per tick and every
/// client receives the SAME bytes; the encoded payload is the documented wire
/// shape; and a real /ws client still receives parseable snapshots.
/// </summary>
public sealed class WireSnapshotTests
{
    private sealed class WireCapturingClient : ISessionClient
    {
        private readonly List<WireSnapshot> _received;
        private readonly SemaphoreSlim _lock;

        public WireCapturingClient(string clientId, List<WireSnapshot> received, SemaphoreSlim gate)
        {
            ClientId = clientId;
            _received = received;
            _lock = gate;
        }

        public string ClientId { get; }

        public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            return SendSnapshotAsync(WireSnapshot.Encode(snapshot), cancellationToken);
        }

        public ValueTask SendSnapshotAsync(WireSnapshot wire, CancellationToken cancellationToken)
        {
            _lock.Wait();
            try
            {
                _received.Add(wire);
            }
            finally
            {
                _lock.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TinyGameModule : IGameModule
    {
        public string Name => "tiny-wire";
        public object GetStateSnapshot() => new { counter = 1 };
        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events) { }
        public void Dispose() { }
    }

    [Fact]
    public async Task BroadcastPath_AllClientsShareTheSameEncodedBytes()
    {
        var received = new List<WireSnapshot>();
        var gate = new SemaphoreSlim(1, 1);
        await using var actor = new SessionActor("s-wire", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 1,
        }, new TinyGameModule());

        await actor.AddClientAsync(new WireCapturingClient("c1", received, gate));
        await actor.AddClientAsync(new WireCapturingClient("c2", received, gate));
        actor.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            gate.Wait();
            bool shared;
            try
            {
                // Exit only when the asserted property already holds: a tick
                // delivered to BOTH clients. Exiting on "2 distinct ticks seen"
                // would race the second client's queue pump (Assertion raced
                // queue delivery in 3/10 runs).
                shared = received.GroupBy(w => w.Snapshot.TickId).Any(g => g.Count() == 2);
            }
            finally
            {
                gate.Release();
            }

            if (shared)
            {
                break;
            }

            await Task.Delay(10);
        }

        gate.Wait();
        try
        {
            var ticks = received.Select(w => w.Snapshot.TickId).Distinct().ToList();
            Assert.True(ticks.Count >= 1, "no snapshot received");

            // Same tick reaching both clients -> same WireSnapshot instance
            // (the latest-wins outbound queue may drop a tick for one client
            // entirely; that's the documented stage-2 behavior, not a reuse
            // violation). Every tick delivers 1 or 2 entries, never >2.
            foreach (var tick in ticks)
            {
                var sameTick = received.Where(w => w.Snapshot.TickId == tick).Select(w => w.EncodedJson).ToList();
                Assert.InRange(sameTick.Count, 1, 2);
                if (sameTick.Count == 2)
                {
                    Assert.Same(sameTick[0], sameTick[1]);
                }
            }

            // At least one tick must reach both clients to prove the reuse path.
            var sharedTicks = ticks.Count(tick =>
                received.Count(w => w.Snapshot.TickId == tick) == 2);
            Assert.True(sharedTicks >= 1, "no tick reached both clients");

            // Wire shape: documented snapshot message with camelCase fields.
            var json = JsonSerializer.Deserialize<JsonElement>(received[0].EncodedJson);
            Assert.Equal("snapshot", json.GetProperty("type").GetString());
            Assert.Equal("s-wire", json.GetProperty("sessionId").GetString());
            Assert.True(json.TryGetProperty("state", out _));
            Assert.True(json.TryGetProperty("serverTimeMs", out _));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task RealWsClient_ReceivesParseableSnapshotFromBytePath()
    {
        using var factory = new WebApplicationFactory<Program>();
        var wsClient = factory.Server.CreateWebSocketClient();
        using var ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, "ws"), CancellationToken.None);

        var hello = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"sessionId\":\"s-wire-e2e\",\"clientId\":\"c-wire\"}");
        await ws.SendAsync(hello, WebSocketMessageType.Text, true, CancellationToken.None);

        // Read until a snapshot arrives (welcome may be interleaved first).
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? snapshotJson = null;
        while (!cts.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var doc = JsonSerializer.Deserialize<JsonElement>(text);
            if (doc.TryGetProperty("type", out var type) && type.GetString() == "snapshot")
            {
                snapshotJson = text;
                break;
            }
        }

        Assert.NotNull(snapshotJson);
        var snap = JsonSerializer.Deserialize<JsonElement>(snapshotJson!);
        Assert.Equal("snapshot", snap.GetProperty("type").GetString());
        Assert.Equal("s-wire-e2e", snap.GetProperty("sessionId").GetString());
        Assert.True(snap.TryGetProperty("state", out var state));
        Assert.Equal(JsonValueKind.Object, state.ValueKind);
        ws.Dispose();
    }
}
