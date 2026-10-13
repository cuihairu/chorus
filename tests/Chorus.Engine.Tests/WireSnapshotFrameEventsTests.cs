using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Engine.Session;
using Chorus.Protocol.Messages;
using Chorus.Protocol.Models;
using Chorus.Server;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Frame-sync P2 wire contract (todo §11): the frameEvents envelope is
/// additive-optional - absent when a snapshot carries no events, camelCase
/// when present, and old payloads without the field still deserialize (no v
/// bump, versioning.md §2). The e2e half proves a real /ws client can pull a
/// replay from frame 1 through the new resync message.
/// </summary>
public sealed class WireSnapshotFrameEventsTests
{
    private static readonly JsonSerializerOptions JsonOpts = Chorus.Protocol.ProtocolJson.SerializerOptions;

    [Fact]
    public void SnapshotWithoutFrameEvents_OmitsTheWireKey()
    {
        var wire = WireSnapshot.Encode(new SessionSnapshot("s", 1, 0, new { counter = 1 }));
        var json = JsonSerializer.Deserialize<JsonElement>(wire.EncodedJson);

        Assert.False(json.TryGetProperty("frameEvents", out _));
    }

    [Fact]
    public void SnapshotWithFrameEvents_SerializesCamelCaseEnvelope()
    {
        var snapshot = new SessionSnapshot(
            SessionId: "s",
            TickId: 7,
            ServerTimeMs: 0,
            State: new { counter = 1 },
            FrameEvents: new[]
            {
                new FrameEvent(FrameId: 7, EventId: "ev-1", Kind: "comment", UserId: "u1"),
                new FrameEvent(FrameId: 7, EventId: "ev-2", Kind: "like", UserId: "u2"),
            });

        var json = JsonSerializer.Deserialize<JsonElement>(WireSnapshot.Encode(snapshot).EncodedJson);

        Assert.True(json.TryGetProperty("frameEvents", out var fe));
        Assert.Equal(JsonValueKind.Array, fe.ValueKind);
        Assert.Equal(2, fe.GetArrayLength());

        var first = fe[0];
        Assert.Equal(7, first.GetProperty("frameId").GetInt64());
        Assert.Equal("ev-1", first.GetProperty("eventId").GetString());
        Assert.Equal("comment", first.GetProperty("kind").GetString());
        Assert.Equal("u1", first.GetProperty("userId").GetString());
    }

    [Fact]
    public void LegacySnapshotJson_WithoutFrameEvents_StillDeserializes()
    {
        // A pre-P2 server payload: no frameEvents key anywhere.
        const string legacy = """
            {"type":"snapshot","sessionId":"s-old","tickId":3,"serverTimeMs":0,"v":1,
             "state":{"counter":1}}
            """;

        var snapshot = JsonSerializer.Deserialize<ServerSnapshot>(legacy, JsonOpts);

        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot!.TickId);
        Assert.Null(snapshot.FrameEvents);
    }

    [Fact]
    public void ClientResync_DeserializesFromWireShape()
    {
        const string wire = """
            {"type":"resync","sessionId":"s","clientId":"c1","fromFrameId":12}
            """;

        var resync = JsonSerializer.Deserialize<ClientResync>(wire, JsonOpts);

        Assert.NotNull(resync);
        Assert.Equal("resync", resync!.Type);
        Assert.Equal("c1", resync.ClientId);
        Assert.Equal(12, resync.FromFrameId);
    }

    [Fact]
    public async Task RealWsClient_Resync_ReplaysRetainedWindowFromFrameOne()
    {
        using var factory = new WebApplicationFactory<Chorus.Server.Program>();
        var wsClient = factory.Server.CreateWebSocketClient();
        using var ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, "ws"), CancellationToken.None);

        var hello = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"sessionId\":\"s-resync-e2e\",\"clientId\":\"c-resync\"}");
        await ws.SendAsync(hello, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[256 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        long firstTick = -1;
        while (firstTick < 0 && !cts.IsCancellationRequested)
        {
            var snap = await ReadSnapshotAsync(ws, buffer, cts.Token);
            if (snap != null)
            {
                firstTick = snap.Value.GetProperty("tickId").GetInt64();
            }
        }

        Assert.True(firstTick >= 1, "no snapshot received");

        // Let a few frames accumulate in the window, then ask for a replay
        // starting at frame 1 - a tick the periodic stream never resends.
        await Task.Delay(300);
        var resync = Encoding.UTF8.GetBytes(
            "{\"type\":\"resync\",\"sessionId\":\"s-resync-e2e\",\"clientId\":\"c-resync\",\"fromFrameId\":1}");
        await ws.SendAsync(resync, WebSocketMessageType.Text, true, cts.Token);

        long replayedTick = -1;
        while (!cts.IsCancellationRequested)
        {
            var snap = await ReadSnapshotAsync(ws, buffer, cts.Token);
            if (snap == null)
            {
                break;
            }

            var tickId = snap.Value.GetProperty("tickId").GetInt64();
            if (tickId == 1)
            {
                replayedTick = tickId;
                break;
            }
        }

        Assert.Equal(1, replayedTick);
        ws.Dispose();
    }

    private static async Task<JsonElement?> ReadSnapshotAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var result = await ws.ReceiveAsync(buffer, ct);
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var doc = JsonSerializer.Deserialize<JsonElement>(text);
        if (doc.TryGetProperty("type", out var type) && type.GetString() == "snapshot")
        {
            return doc;
        }

        return null;
    }
}
