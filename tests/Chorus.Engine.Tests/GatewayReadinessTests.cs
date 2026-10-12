using System.Net.Http.Json;
using System.Text.Json;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// Golden tests for the split-mode readiness probe (todo §8 P3 运维):
/// /ready fails closed before the first engine message and after the silence
/// window, goes 200 while engine messages flow, the silence gauge is exported,
/// and EngineIpcService actually emits eng.heartbeat messages on the eng→gw
/// channel so /ready stays honest even with no clients connected.
/// </summary>
public sealed class GatewayReadinessTests
{
    public sealed class GatewayFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Tight window so the stale case needs no real waiting.
            builder.UseSetting("Ipc:ReadinessMaxSilenceMs", "50");
            builder.UseSetting("Ipc:Transport", "Auto");
        }
    }

    // ---------------------------------------------------------------- /ready e2e

    [Fact]
    public async Task Ready_BeforeFirstEngineMessage_FailsClosed()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/ready");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ready").GetBoolean());
        Assert.Equal(-1, body.GetProperty("engineSilenceMs").GetInt64());
    }

    [Fact]
    public async Task Ready_WithFreshEngineMessage_Returns200()
    {
        using var factory = new GatewayFactory();
        var ipc = factory.Services.GetRequiredService<GatewayIpcService>();

        ipc.RecordEngineMessage(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var response = await factory.CreateClient().GetAsync("/ready");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ready").GetBoolean());
        Assert.True(body.GetProperty("engineSilenceMs").GetInt64() <= 50);
    }

    [Fact]
    public async Task Ready_AfterSilenceWindowExpires_Returns503()
    {
        using var factory = new GatewayFactory();
        var ipc = factory.Services.GetRequiredService<GatewayIpcService>();

        // Window is 50ms; a message stamped in the past must flip back to 503.
        var stale = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        ipc.RecordEngineMessage(stale);
        var response = await factory.CreateClient().GetAsync("/ready");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Ready_MetricsExposeSilenceGauge()
    {
        using var factory = new GatewayFactory();
        var ipc = factory.Services.GetRequiredService<GatewayIpcService>();
        ipc.RecordEngineMessage(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var metrics = await factory.CreateClient().GetStringAsync("/metrics");

        Assert.Contains("chorus_ipc_engine_silence_ms", metrics);
    }

    // ---------------------------------------------------------------- engine heartbeat emission

    [Fact]
    public async Task EngineIpcService_PublishesHeartbeats_OnEngToGwChannel()
    {
        // Unique socket dir so the engine binds fresh endpoints; the collector
        // plays the Gateway role (server) on the eng→gw channel.
        var socketDir = $"/tmp/chorus-test-hb-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engToGwPath = Path.Combine(socketDir, "chorus_eng_to_gw.sock");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ipc:Transport"] = "Auto",
                ["Ipc:UnixSocketDir"] = socketDir,
                ["Ipc:HeartbeatIntervalMs"] = "20",
            })
            .Build();

        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessGameModuleFactory(),
            new SessionConfig(),
            config);

        var received = new List<Envelope>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var collector = new UnixDomainSocketMessageBus(engToGwPath);
        collector.StartServer();
        try
        {
            var collectLoop = Task.Run(async () =>
            {
                await foreach (var env in collector.SubscribeAsync(cts.Token))
                {
                    lock (received) { received.Add(env); }
                }
            });

            await engine.StartAsync(cts.Token);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var gotHeartbeat = false;
            while (sw.ElapsedMilliseconds < 10_000)
            {
                lock (received) { gotHeartbeat = received.Any(e => e.Type == IpcMessageTypes.EngineHeartbeat); }
                if (gotHeartbeat) break;
                await Task.Delay(10);
            }

            Assert.True(gotHeartbeat, "no eng.heartbeat arrived within 10s");
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
            cts.Cancel();
            await collector.DisposeAsync();
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    private sealed class HeadlessGameModuleFactory : IGameModuleFactory
    {
        public IGameModule Create(string sessionId) => new HeadlessGameModule();
    }

    private sealed class HeadlessGameModule : IGameModule
    {
        public string Name => "headless";
        public object GetStateSnapshot() => new { };
        public void ApplyEvents(long tickId, System.Collections.Generic.IReadOnlyList<Chorus.Protocol.Models.AudienceEvent> events) { }
        public void Dispose() { }
    }
}
