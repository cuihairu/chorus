using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol;
using Chorus.Protocol.Messages;
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
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }

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
    public void Encode_CarriesSchemaVersion_V()
    {
        // versioning.md §2: the snapshot schema version rides the wire
        // protocol as `v`, passed through from the engine snapshot — the
        // encode step must not hardcode it.
        var snapshot = new SessionSnapshot(
            SessionId: "s-v",
            TickId: 1,
            ServerTimeMs: 0,
            State: new { counter = 1 });

        var json = JsonSerializer.Deserialize<JsonElement>(WireSnapshot.Encode(snapshot).EncodedJson);
        Assert.Equal(1, json.GetProperty("v").GetInt32());

        var promoted = snapshot with { V = 2 };
        var promotedJson = JsonSerializer.Deserialize<JsonElement>(WireSnapshot.Encode(promoted).EncodedJson);
        Assert.Equal(2, promotedJson.GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task RealWsClient_ReceivesParseableSnapshotFromBytePath()
    {
        using var factory = new WebApplicationFactory<Chorus.Server.Program>();
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

    // ---- State encoding (2026-10-20, todo.md:159 tick-body CPU) ----
    // The payload's State used to be materialized as a JsonElement DOM and then
    // serialized again, so every snapshot visited the state twice. The inline
    // write must produce the exact same wire bytes.

    private static object RichState() => new
    {
        counter = 12,
        flag = true,
        missing = (string?)null,
        players = new[]
        {
            new { id = 7, x = 1.5, name = "甲" },
            new { id = 8, x = -0.25, name = "b" },
        },
        empty = Array.Empty<int>(),
    };

    /// <summary>The pre-optimization encoder: State built as a DOM, then serialized.</summary>
    private static ServerSnapshot LegacyPayload(SessionSnapshot snapshot) => new(
        Type: ProtocolTypes.Snapshot,
        SessionId: snapshot.SessionId,
        TickId: snapshot.TickId,
        ServerTimeMs: snapshot.ServerTimeMs,
        State: JsonSerializer.SerializeToElement(snapshot.State, ProtocolJson.SerializerOptions),
        Forced: snapshot.Forced,
        Reason: snapshot.Reason,
        TargetClientId: snapshot.TargetClientId,
        ClientLagTicks: snapshot.ClientLagTicks,
        ClientLastAckTickId: snapshot.ClientLastAckTickId,
        V: snapshot.V,
        FrameEvents: snapshot.FrameEvents is { Count: > 0 } ? new List<FrameEvent>(snapshot.FrameEvents) : null);

    [Fact]
    public void Encode_StateWrittenInline_MatchesLegacyDomPath_ByteForByte()
    {
        var states = new object[] { RichState(), new { }, new { a = 1, b = "x" }, new { list = new[] { 1, 2, 3 } } };
        foreach (var state in states)
        {
            foreach (var forced in new bool?[] { null, false, true })
            {
                var snapshot = new SessionSnapshot(
                    SessionId: "s-enc", TickId: 5, ServerTimeMs: 1000, State: state, Forced: forced);

                var actual = WireSnapshot.Encode(snapshot).EncodedJson;
                var legacy = JsonSerializer.SerializeToUtf8Bytes(LegacyPayload(snapshot), ProtocolJson.SerializerOptions);

                Assert.True(legacy.SequenceEqual(actual),
                    $"wire bytes drifted (forced={forced}): legacy={Encoding.UTF8.GetString(legacy)}"
                    + $" actual={Encoding.UTF8.GetString(actual)}");
            }
        }
    }

    [Fact]
    public void Encode_RoundTrip_StateStillDeserializesAsJsonElement()
    {
        // ServerSnapshot crosses eng→gw IPC and the gateway re-serializes it, and
        // module restore reads SnapshotRecovery.State as a JsonElement. An
        // object-typed field materializes as a JsonElement when deserialized, so
        // every read site keeps its current runtime shape.
        var snapshot = new SessionSnapshot(
            SessionId: "s-rt",
            TickId: 3,
            ServerTimeMs: 0,
            State: RichState(),
            Forced: true,
            Reason: "lag",
            FrameEvents: new List<FrameEvent> { new(3, "e-1", "input", "u-1") });

        var roundTripped = JsonSerializer.Deserialize<ServerSnapshot>(
            WireSnapshot.Encode(snapshot).EncodedJson, ProtocolJson.SerializerOptions)!;

        // The runtime shape at every read site is still a JsonElement.
        var state = Assert.IsType<JsonElement>(roundTripped.State);

        Assert.Equal(JsonValueKind.Object, state.ValueKind);
        Assert.Equal(12, state.GetProperty("counter").GetInt32());
        Assert.Equal(2, state.GetProperty("players").GetArrayLength());
        Assert.Equal("甲", state.GetProperty("players")[0].GetProperty("name").GetString());
        Assert.Equal("snapshot", roundTripped.Type);
        Assert.Equal(3, roundTripped.TickId);
        Assert.True(roundTripped.Forced);
        Assert.Equal("lag", roundTripped.Reason);
        Assert.Single(roundTripped.FrameEvents);
    }

    [Fact]
    public async Task MixedTick_NonForcedClientsShareBroadcastBytes()
    {
        // Churn path (periodic tick plus some lagging clients): a non-forced
        // client receives exactly the broadcast payload, so all non-forced
        // clients must share ONE encoded wire - only the forced client's
        // payload differs (targetClientId / lag fields are per client).
        var received = new List<WireSnapshot>();
        var gate = new SemaphoreSlim(1, 1);
        await using var actor = new SessionActor("s-mixed", new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 1,
            MaxLagTicks = 5,
        }, new TinyGameModule());
        using var stop = new CancellationTokenSource();

        await actor.AddClientAsync(new WireCapturingClient("c-ack1", received, gate));
        await actor.AddClientAsync(new WireCapturingClient("c-ack2", received, gate));
        await actor.AddClientAsync(new WireCapturingClient("c-lag", received, gate));
        actor.Start();

        // Ackers keep up; c-lag never acks, so its lag crosses MaxLagTicks and
        // it gets the forced (targeted) snapshot while the others stay on the
        // broadcast wire.
        var acker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var tick = actor.Diagnostics.TickId;
                await actor.AckAsync("c-ack1", tick, 0);
                await actor.AckAsync("c-ack2", tick, 0);
                await Task.Delay(5, stop.Token);
            }
        });

        try
        {
            (long tickId, WireSnapshot ack1, WireSnapshot ack2, WireSnapshot lag) mixed = default;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                gate.Wait();
                List<WireSnapshot> snapshot;
                try
                {
                    snapshot = received.ToList();
                }
                finally
                {
                    gate.Release();
                }

                foreach (var group in snapshot.GroupBy(w => w.Snapshot.TickId))
                {
                    // Identify by payload shape: the forced wire is the one
                    // carrying a targetClientId, broadcast wires carry none.
                    var wires = group.ToList();
                    var forced = wires.Where(w =>
                    {
                        var json = JsonSerializer.Deserialize<JsonElement>(w.EncodedJson);
                        return json.TryGetProperty("targetClientId", out _);
                    }).ToList();
                    var broadcast = wires.Where(w =>
                    {
                        var json = JsonSerializer.Deserialize<JsonElement>(w.EncodedJson);
                        return !json.TryGetProperty("targetClientId", out _);
                    }).ToList();

                    if (forced.Count >= 1 && broadcast.Count >= 2)
                    {
                        mixed = (group.Key, broadcast[0], broadcast[1], forced[0]);
                        break;
                    }
                }

                if (mixed.ack1 is not null)
                {
                    break;
                }

                await Task.Delay(10, stop.Token);
            }

            Assert.True(mixed.ack1 is not null, "no mixed tick (forced + broadcast) observed within 5s");

            // The two non-forced clients received the SAME encoded wire object:
            // encode once, reuse everywhere.
            Assert.Same(mixed.ack1, mixed.ack2);

            var forcedJson = JsonSerializer.Deserialize<JsonElement>(mixed.lag.EncodedJson);
            Assert.True(forcedJson.GetProperty("forced").GetBoolean());
            Assert.Equal("lag", forcedJson.GetProperty("reason").GetString());
            Assert.Equal("c-lag", forcedJson.GetProperty("targetClientId").GetString());
            Assert.True(forcedJson.TryGetProperty("clientLagTicks", out _));

            var ackJson = JsonSerializer.Deserialize<JsonElement>(mixed.ack1.EncodedJson);
            Assert.False(ackJson.TryGetProperty("forced", out var forcedField) && forcedField.GetBoolean());
            Assert.False(ackJson.TryGetProperty("targetClientId", out _));
        }
        finally
        {
            stop.Cancel();
            try
            {
                await acker;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
