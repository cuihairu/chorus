using System.Net;
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
/// Stage-2 wrap-up guard (design/process-topology.md §4, todo 进程拓扑): the
/// documented three-process shape exercised for real — one EngineHost
/// (room-worker) with TWO gateway processes on the same IPC endpoint, one in
/// the platform role (ingress only) and one in the streamer role (WS only).
/// An event POSTed to the platform-gw must surface in a snapshot on a WS
/// client of the streamer-gw; each gateway omits the other role's endpoint
/// even in this live shape. The bus is broadcast, and each gateway filters by
/// its own live clients — this test pins that.
/// </summary>
public sealed class ThreeProcessEndToEndTests
{
    private sealed class ThreeProcessGatewayFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        private readonly string _socketDir;
        private readonly string _roles;
        private readonly string _gatewayId;

        public ThreeProcessGatewayFactory(string socketDir, string roles, string gatewayId)
        {
            _socketDir = socketDir;
            _roles = roles;
            _gatewayId = gatewayId;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
            builder.UseSetting("Ipc:UnixSocketDir", _socketDir);
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "true");
            builder.UseSetting("Ingress:AllowAll", "true");
            builder.UseSetting("Gateway:Roles", _roles);
            builder.UseSetting("Gateway:Id", _gatewayId);
        }
    }

    private sealed class CountingModule : IGameModule
    {
        private long _applied;

        public string Name => "counting-3proc";
        public object GetStateSnapshot() => new { applied = Interlocked.Read(ref _applied) };
        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
            if (events.Count > 0)
            {
                Interlocked.Add(ref _applied, events.Count);
            }
        }

        public void Dispose() { }
    }

    private sealed class CountingModuleFactory : IGameModuleFactory
    {
        public CountingModule Module { get; } = new();
        public IGameModule Create(string sessionId) => Module;
    }

    private static async Task WaitReadyAsync(WebApplicationFactory<Chorus.Gateway.Program> factory, CancellationToken ct)
    {
        // Two hosts + the engine spin up together on a box shared with other
        // tenants; don't assume startup latency — wide window, poll /ready.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var probe = await factory.CreateClient().GetAsync("/ready", ct);
            if (probe.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "gateway never reported the engine ready");
            await Task.Delay(20, ct);
        }
    }

    private static async Task<JsonElement> ReceiveUntilAsync(WebSocket ws, Func<JsonElement, bool> predicate, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException($"connection closed ({ws.CloseStatus}) while waiting");
            }

            JsonElement doc;
            try
            {
                doc = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(buffer, 0, result.Count));
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
    public async Task PlatformGwIngress_SurfacesOnStreamerGwClient()
    {
        var socketDir = $"/tmp/chorus-test-3proc-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engineConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ipc:Transport"] = "UnixDomainSocket",
                ["Ipc:UnixSocketDir"] = socketDir,
                ["Ipc:HeartbeatIntervalMs"] = "100",
                ["Ipc:GatewayIds"] = "platform-gw,streamer-gw",
            })
            .Build();
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new CountingModuleFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            engineConfig);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await engine.StartAsync(cts.Token);
        try
        {
            using var platformGw = new ThreeProcessGatewayFactory(socketDir, "platform", "platform-gw");
            using var streamerGw = new ThreeProcessGatewayFactory(socketDir, "streamer", "streamer-gw");
            await WaitReadyAsync(platformGw, cts.Token);
            await WaitReadyAsync(streamerGw, cts.Token);

            // Live three-process shape: each gateway omits the other role's
            // endpoint (role packaging under real wiring, not just gating).
            var platformIngress = await platformGw.CreateClient().PostAsync("/ingress/test", new StringContent(
                "{}", Encoding.UTF8, "application/json"), cts.Token);
            Assert.Equal(HttpStatusCode.BadRequest, platformIngress.StatusCode);
            var streamerIngress = await streamerGw.CreateClient().PostAsync("/ingress/test", new StringContent(
                "{}", Encoding.UTF8, "application/json"), cts.Token);
            Assert.Equal(HttpStatusCode.NotFound, streamerIngress.StatusCode);

            var wsClient = streamerGw.Server.CreateWebSocketClient();
            WebSocket? ws = null;
            // The 2s welcome budget can race host load; retry the handshake
            // like a real client (same discipline as the split-mode e2e).
            for (var attempt = 1; attempt <= 5 && ws == null; attempt++)
            {
                try
                {
                    ws = await wsClient.ConnectAsync(new Uri(streamerGw.Server.BaseAddress, "ws"), cts.Token);
                    var hello = Encoding.UTF8.GetBytes(
                        """{"type":"hello","sessionId":"s-3proc","clientId":"c-3proc","protocolVersion":1}""");
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

            using (ws)
            {
                // The event enters through the OTHER gateway process.
                var post = await platformGw.CreateClient().PostAsync("/ingress/test", new StringContent(
                    """{"sessionId":"s-3proc","eventId":"ev-3proc-1","userId":"u1","kind":"comment","text":"hi"}""",
                    Encoding.UTF8,
                    "application/json"), cts.Token);
                Assert.Equal(HttpStatusCode.OK, post.StatusCode);

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

                Assert.True(applied >= 1, $"event posted to platform-gw never surfaced on streamer-gw client (applied={applied})");
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
