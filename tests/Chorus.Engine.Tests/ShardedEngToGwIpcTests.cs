using System.Net.WebSockets;
using System.Text;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway;
using Chorus.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// Sharded eng-to-gw wiring (todo §10 blocker ②), over real Unix domain
/// sockets: the engine publishes each session's frames to the shard its id
/// hashes to (Ipc:EngToGwShards on both ends); the gateway hosts one server
/// socket per shard and merges them into its single dispatch loop. A session
/// on every shard index gets a welcome and snapshots — this proves the
/// engine-side routing, the per-shard endpoint names and the gateway-side
/// fan-in all wired up, not just that the buses work in isolation.
/// </summary>
public sealed class ShardedEngToGwIpcTests
{
    private const int Shards = 3;

    [Fact]
    public async Task SessionsOnEveryShard_ReceiveWelcomeAndSnapshots()
    {
        var socketDir = $"/tmp/chorus-test-sharded-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = CreateEngine(socketDir);
        using var gatewayHost = new ShardedGatewayFactory(socketDir);

        // One session per shard index, picked with the same stable hash the
        // engine routes by: no cross-shard luck, every socket must deliver.
        var sessionsByShard = new string[Shards];
        for (var i = 0; i < 1000 && sessionsByShard.Any(s => s is null); i++)
        {
            var candidate = $"shard-e2e-{i}";
            var shard = ShardedMessageBus.ShardIndex(candidate, Shards);
            sessionsByShard[shard] ??= candidate;
        }

        Assert.All(sessionsByShard, s => Assert.NotNull(s));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await engine.StartAsync(cts.Token);
            _ = gatewayHost.Server;
            await WaitGatewayReadyAsync(gatewayHost, cts.Token);

            foreach (var session in sessionsByShard)
            {
                using var ws = await ConnectWithRetryAsync(gatewayHost, session!, "client-a", cts.Token);
                var snapshot = await ReceiveSnapshotAsync(ws, cts.Token);
                Assert.Equal(session, snapshot.GetProperty("sessionId").GetString());
            }

            // The per-shard sockets really exist under the sharded names
            // (legacy unsuffixed name would mean the split never happened).
            for (var shard = 0; shard < Shards; shard++)
            {
                Assert.True(
                    File.Exists(Path.Combine(socketDir, $"chorus_eng_to_gw_{shard}.sock")),
                    $"missing shard socket {shard} in {socketDir}");
            }
        }
        finally
        {
            await gatewayHost.DisposeAsync();
            await engine.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    private static EngineIpcService CreateEngine(string socketDir)
    {
        return new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new CountingModuleFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ipc:Transport"] = "UnixDomainSocket",
                    ["Ipc:UnixSocketDir"] = socketDir,
                    ["Ipc:HeartbeatIntervalMs"] = "50",
                    ["Ipc:EngToGwShards"] = Shards.ToString(),
                })
                .Build());
    }

    private static async Task<WebSocket> ConnectWithRetryAsync(
        WebApplicationFactory<Chorus.Gateway.Program> host,
        string sessionId,
        string clientId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            WebSocket? ws = null;
            try
            {
                ws = await host.Server.CreateWebSocketClient().ConnectAsync(
                    new Uri(host.Server.BaseAddress, "/ws"), cancellationToken);
                var hello = Encoding.UTF8.GetBytes(
                    $$"""{"type":"hello","sessionId":"{{sessionId}}","clientId":"{{clientId}}","protocolVersion":1}""");
                await ws.SendAsync(hello, WebSocketMessageType.Text, true, cancellationToken);
                var welcome = await ReceiveMessageAsync(ws, cancellationToken);
                Assert.Equal("welcome", welcome.GetProperty("type").GetString());
                return ws;
            }
            catch (Exception) when (attempt < 5)
            {
                ws?.Dispose();
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static async Task<System.Text.Json.JsonElement> ReceiveMessageAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Gateway closed the sharded session.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                using var document = System.Text.Json.JsonDocument.Parse(message.ToArray());
                return document.RootElement.Clone();
            }
        }
    }

    private static async Task<System.Text.Json.JsonElement> ReceiveSnapshotAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReceiveMessageAsync(ws, cancellationToken);
            if (message.TryGetProperty("type", out var type) && type.GetString() == "snapshot")
            {
                return message;
            }
        }

        throw new TimeoutException("no snapshot through the sharded channel within 20s");
    }

    private static async Task WaitGatewayReadyAsync(
        WebApplicationFactory<Chorus.Gateway.Program> host,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                using var response = await host.CreateClient().GetAsync("/ready", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("sharded gateway never became ready");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class ShardedGatewayFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        private readonly string _socketDir;

        public ShardedGatewayFactory(string socketDir)
        {
            _socketDir = socketDir;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
            builder.UseSetting("Ipc:UnixSocketDir", _socketDir);
            builder.UseSetting("Ipc:EngToGwShards", Shards.ToString());
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "true");
        }
    }

    private sealed class CountingModule : IGameModule
    {
        private long _applied;

        public string Name => "sharded-counting";

        public object GetStateSnapshot() => new { applied = Interlocked.Read(ref _applied) };

        public void ApplyEvents(long tickId, IReadOnlyList<Chorus.Protocol.Models.AudienceEvent> events)
        {
            Interlocked.Add(ref _applied, events.Count);
        }

        public void RestoreState(long version, System.Text.Json.JsonElement state)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingModuleFactory : IGameModuleFactory
    {
        public IGameModule Create(string sessionId) => new CountingModule();
    }
}
