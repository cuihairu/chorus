using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway;
using Chorus.Protocol.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// Split-mode main-path e2e (todo P1 测试缺口): a real WS client connects to
/// the Gateway, the gateway bridges hello/welcome and snapshots over the
/// eng&lt;-&gt;gw UDS channel to a live EngineIpcService, and an audience event
/// POSTed to the HTTP ingress surfaces in the engine's snapshot state - the
/// WS→IPC→Engine→IPC→WS round trip, end to end.
/// </summary>
public sealed class SplitModeEndToEndTests
{
    public sealed class E2EGatewayFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        private readonly string _socketDir;

        public E2EGatewayFactory(string socketDir)
        {
            _socketDir = socketDir;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
            builder.UseSetting("Ipc:UnixSocketDir", _socketDir);
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "true");
            builder.UseSetting("Ingress:AllowAll", "true");
        }
    }

    private sealed class CountingModule : IGameModule
    {
        private long _applied;

        public string Name => "counting-e2e";
        public object GetStateSnapshot() => new { applied = Interlocked.Read(ref _applied) };
        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
            if (events.Count > 0)
            {
                Interlocked.Add(ref _applied, events.Count);
            }
        }

        public void RestoreState(long version, System.Text.Json.JsonElement state) { }

        public void Dispose() { }
    }

    private sealed class CountingModuleFactory : IGameModuleFactory
    {
        public CountingModule Module { get; } = new();
        public IGameModule Create(string sessionId) => Module;
    }

    private static async Task<JsonElement> ReceiveUntilAsync(WebSocket ws, Func<JsonElement, bool> predicate, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var seen = new List<string>();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException(
                    $"connection closed ({ws.CloseStatus}) while waiting; messages seen: [{string.Join(", ", seen)}]");
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            seen.Add(text);
            JsonElement doc;
            try
            {
                doc = JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (JsonException)
            {
                continue;
            }

            if (predicate(doc))
            {
                return doc;
            }
        }
    }

    [Fact]
    public async Task WsClient_ReachesEngineOverIpc_WelcomeSnapshotAndIngressEvent()
    {
        var socketDir = $"/tmp/chorus-test-e2e-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engineConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ipc:Transport"] = "UnixDomainSocket",
                ["Ipc:UnixSocketDir"] = socketDir,
                ["Ipc:HeartbeatIntervalMs"] = "100",
            })
            .Build();
        var moduleFactory = new CountingModuleFactory();
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            moduleFactory,
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            engineConfig);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            using var factory = new E2EGatewayFactory(socketDir);

            // Warm the IPC channel before the hello: /ready returns 200 only
            // once engine heartbeats are flowing through the gateway's
            // eng->gw server, so both bus connections are established and the
            // 2s welcome timeout can't race the first publish under load.
            var readyDeadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var probe = await factory.CreateClient().GetAsync("/ready", cts.Token);
                if (probe.StatusCode == HttpStatusCode.OK)
                {
                    break;
                }

                Assert.True(DateTime.UtcNow < readyDeadline, "gateway never reported the engine ready");
                await Task.Delay(20, cts.Token);
            }

            var wsClient = factory.Server.CreateWebSocketClient();
            WebSocket? ws = null;
            JsonElement welcome = default;

            // Connect + hello -> GatewayConnect over gw->eng -> engine welcome
            // back over eng->gw. The gateway's 2s welcome timeout is a
            // production limit a real client can hit under host load, so
            // reconnect and retry like a real client instead of assuming the
            // first attempt lands.
            for (var attempt = 1; attempt <= 5 && ws == null; attempt++)
            {
                try
                {
                    ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, "ws"), cts.Token);
                    var hello = Encoding.UTF8.GetBytes(
                        """{"type":"hello","sessionId":"s-e2e","clientId":"c-e2e","protocolVersion":1}""");
                    await ws.SendAsync(hello, WebSocketMessageType.Text, true, cts.Token);

                    welcome = await ReceiveUntilAsync(ws, d => d.TryGetProperty("type", out var t) && t.GetString() == "welcome", cts.Token);
                }
                catch (Exception)
                {
                    if (attempt == 5)
                    {
                        throw;
                    }

                    ws?.Dispose();
                    ws = null;
                    await Task.Delay(50, cts.Token);
                }
            }

            using (ws)
            {
                Assert.Equal("s-e2e", welcome.GetProperty("sessionId").GetString());
                Assert.Equal("c-e2e", welcome.GetProperty("clientId").GetString());
                Assert.Equal(1, welcome.GetProperty("protocolVersion").GetInt32());

                // Engine tick snapshots land on the WS client through the same channel.
                var snapshot = await ReceiveUntilAsync(ws!, d => d.TryGetProperty("type", out var t) && t.GetString() == "snapshot", cts.Token);
                Assert.Equal("s-e2e", snapshot.GetProperty("sessionId").GetString());
                Assert.True(snapshot.TryGetProperty("state", out _));

                // Audience event: HTTP ingress -> GatewayAudienceEvent -> engine ingest
                // -> applied by the module -> visible in a subsequent snapshot state.
                var post = await factory.CreateClient().PostAsync("/ingress/test", new StringContent(
                    """{"sessionId":"s-e2e","eventId":"ev-e2e-1","userId":"u1","kind":"comment","text":"hi"}""",
                    Encoding.UTF8,
                    "application/json"), cts.Token);
                Assert.Equal(HttpStatusCode.OK, post.StatusCode);
                var ack = await post.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                Assert.True(ack.GetProperty("ok").GetBoolean());

                var deadline = DateTime.UtcNow.AddSeconds(15);
                var applied = 0L;
                while (DateTime.UtcNow < deadline && applied < 1)
                {
                    try
                    {
                        var next = await ReceiveUntilAsync(
                            ws!,
                            d => d.TryGetProperty("type", out var t) && t.GetString() == "snapshot",
                            cts.Token);
                        if (next.TryGetProperty("state", out var state) && state.TryGetProperty("applied", out var a))
                        {
                            applied = a.GetInt64();
                        }
                    }
                    catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                }

                Assert.True(applied >= 1, $"audience event never surfaced in snapshot state (applied={applied})");
            }
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Broadcast dedup acceptance (shared-memory-transport.md alt 1): with the
    /// engine publishing one snapshot per session per tick instead of one per
    /// client, every connected client of that session must still receive it —
    /// the gateway fans the null-target snapshot out to all session clients.
    /// </summary>
    [Fact]
    public async Task TwoClientsSameSession_BothReceiveSnapshots()
    {
        var socketDir = $"/tmp/chorus-test-e2e-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engineConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ipc:Transport"] = "UnixDomainSocket",
                ["Ipc:UnixSocketDir"] = socketDir,
                ["Ipc:HeartbeatIntervalMs"] = "100",
            })
            .Build();
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new CountingModuleFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            engineConfig);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            using var factory = new E2EGatewayFactory(socketDir);
            var readyDeadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var probe = await factory.CreateClient().GetAsync("/ready", cts.Token);
                if (probe.StatusCode == HttpStatusCode.OK)
                {
                    break;
                }

                Assert.True(DateTime.UtcNow < readyDeadline, "gateway never reported the engine ready");
                await Task.Delay(20, cts.Token);
            }

            var wsClient = factory.Server.CreateWebSocketClient();
            var sockets = new List<WebSocket>();
            for (var i = 1; i <= 2; i++)
            {
                WebSocket? ws = null;
                // The gateway's 2s welcome budget can race host load; retry
                // the whole handshake like a real client (same as the
                // single-client test above).
                for (var attempt = 1; attempt <= 5 && ws == null; attempt++)
                {
                    try
                    {
                        ws = await wsClient.ConnectAsync(new Uri(factory.Server.BaseAddress, "ws"), cts.Token);
                        var hello = Encoding.UTF8.GetBytes(
                            "{\"type\":\"hello\",\"sessionId\":\"s-e2e-duo\",\"clientId\":\"c-duo-" + i + "\",\"protocolVersion\":1}");
                        await ws.SendAsync(hello, WebSocketMessageType.Text, true, cts.Token);
                        await ReceiveUntilAsync(ws, d => d.TryGetProperty("type", out var t) && t.GetString() == "welcome", cts.Token);
                    }
                    catch (Exception)
                    {
                        if (attempt == 5)
                        {
                            throw;
                        }

                        ws?.Dispose();
                        ws = null;
                        await Task.Delay(50, cts.Token);
                    }
                }

                sockets.Add(ws!);
            }

            // Both clients of the session must see the (single) broadcast.
            var tasks = sockets
                .Select(ws => ReceiveUntilAsync(ws, d => d.TryGetProperty("type", out var t) && t.GetString() == "snapshot", cts.Token))
                .ToList();
            await Task.WhenAll(tasks);
            foreach (var task in tasks)
            {
                Assert.Equal("s-e2e-duo", (await task).GetProperty("sessionId").GetString());
            }

            foreach (var ws in sockets)
            {
                ws.Dispose();
            }
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }
}
