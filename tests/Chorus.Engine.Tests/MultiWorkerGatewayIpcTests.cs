using System.Net.WebSockets;
using System.Text;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Routing;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway;
using Chorus.Gateway.Core.Faces;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol;
using Chorus.Protocol.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

public sealed class MultiWorkerGatewayIpcTests
{
    [Theory]
    [InlineData("session-a")]
    [InlineData("session-b")]
    public void RouteWorker_MatchesRoomAffinity(string sessionId)
    {
        var socketDir = $"/tmp/chorus-test-multi-route-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var gateway = CreateGateway(socketDir);
        try
        {
            var expected = RoomAffinity.Assign(sessionId, ["worker-a", "worker-b"]);

            Assert.Equal(expected, gateway.RouteWorker(sessionId));
        }
        finally
        {
            Directory.Delete(socketDir, recursive: true);
        }
    }

    [Fact]
    public void RouteWorker_NoConfiguredWorker_ReturnsNull()
    {
        var socketDir = $"/tmp/chorus-test-multi-empty-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var gateway = CreateGateway(socketDir, workerIds: "");
        try
        {
            Assert.Null(gateway.RouteWorker("session-a"));
        }
        finally
        {
            Directory.Delete(socketDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectAsync_RoutesEachSessionToAffinityWorker()
    {
        // All three processes share one UDS directory: the gateway's
        // eng->gw server socket is shared by both engine clients, and the
        // per-worker gw->eng sockets are disambiguated by the WorkerId
        // suffix - separate directories would never wire up over AF_UNIX.
        var socketDir = $"/tmp/chorus-test-multi-e2e-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engineA = CreateEngine(socketDir, "worker-a");
        var engineB = CreateEngine(socketDir, "worker-b");
        using var gatewayHost = new MultiWorkerGatewayFactory(socketDir);
        string? sessionIdA = null;
        string? sessionIdB = null;
        for (var i = 0; i < 1000 && (sessionIdA is null || sessionIdB is null); i++)
        {
            var candidate = $"multi-{i}";
            var owner = RoomAffinity.Assign(candidate, ["worker-a", "worker-b"]);
            if (owner == "worker-a") sessionIdA ??= candidate;
            if (owner == "worker-b") sessionIdB ??= candidate;
        }

        Assert.NotNull(sessionIdA);
        Assert.NotNull(sessionIdB);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await engineA.StartAsync(cts.Token);
            await engineB.StartAsync(cts.Token);
            _ = gatewayHost.Server;
            await WaitGatewayReadyAsync(gatewayHost, cts.Token);

            var workerA = await ConnectWithRetryAsync(gatewayHost, sessionIdA!, "client-a", cts.Token);
            var workerB = await ConnectWithRetryAsync(gatewayHost, sessionIdB!, "client-b", cts.Token);
            using (workerA)
            using (workerB)
            {
                var snapshotA = await ReceiveSnapshotAsync(workerA, cts.Token);
                var snapshotB = await ReceiveSnapshotAsync(workerB, cts.Token);

                // The module tags its state with the hosting worker: this proves
                // the hello actually crossed the affinity-suffixed per-worker
                // socket, not just that RoomAffinity computed an id.
                Assert.Equal("worker-a", snapshotA.GetProperty("state").GetProperty("worker").GetString());
                Assert.Equal("worker-b", snapshotB.GetProperty("state").GetProperty("worker").GetString());
            }

            Assert.Equal("worker-a", RoomAffinity.Assign(sessionIdA!, ["worker-a", "worker-b"]));
            Assert.Equal("worker-b", RoomAffinity.Assign(sessionIdB!, ["worker-a", "worker-b"]));
        }
        finally
        {
            await gatewayHost.DisposeAsync();
            await engineA.StopAsync(CancellationToken.None);
            await engineB.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AudienceEvent_RoutesToAffinityWorker()
    {
        var socketDir = $"/tmp/chorus-test-multi-event-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engineA = CreateEngine(socketDir, "worker-a");
        var engineB = CreateEngine(socketDir, "worker-b");
        using var gatewayHost = new MultiWorkerGatewayFactory(socketDir);
        var sessionId = Enumerable.Range(0, 1000)
            .Select(i => $"event-{i}")
            .First(id => RoomAffinity.Assign(id, ["worker-a", "worker-b"]) == "worker-a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await engineA.StartAsync(cts.Token);
            await engineB.StartAsync(cts.Token);
            _ = gatewayHost.Server;
            await WaitGatewayReadyAsync(gatewayHost, cts.Token);

            using var ws = await ConnectWithRetryAsync(gatewayHost, sessionId, "client-a", cts.Token);
            await ReceiveSnapshotAsync(ws, cts.Token);

            var gateway = gatewayHost.Services.GetRequiredService<GatewayIpcService>();
            var audienceEvent = new AudienceEvent(
                EventId: "multi-worker-event-1",
                Platform: "test",
                SessionId: sessionId,
                UserId: "viewer-a",
                Kind: AudienceEventKind.Comment,
                IngestTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Text: "affinity");
            var envelope = EnvelopeFactory.Create(
                IpcMessageTypes.GatewayAudienceEvent,
                sessionId,
                audienceEvent,
                sourceEventId: audienceEvent.EventId);
            await gateway.PublishAudienceEventAsync(envelope, cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                var snapshot = await ReceiveSnapshotAsync(ws, cts.Token);
                if (snapshot.TryGetProperty("state", out var state)
                    && state.TryGetProperty("applied", out var applied)
                    && applied.GetInt64() >= 1)
                {
                    Assert.Equal("worker-a", state.GetProperty("worker").GetString());
                    return;
                }

                Assert.True(DateTime.UtcNow < deadline, "audience event was not routed to the affinity worker");
            }
        }
        finally
        {
            await gatewayHost.DisposeAsync();
            await engineA.StopAsync(CancellationToken.None);
            await engineB.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    private static GatewayIpcService CreateGateway(
        string socketDir,
        string workerIds = "worker-a,worker-b")
    {
        return new GatewayIpcService(
            NullLogger<GatewayIpcService>.Instance,
            GatewayConfiguration(socketDir, workerIds));
    }

    private static EngineIpcService CreateEngine(string socketDir, string workerId)
    {
        Directory.CreateDirectory(socketDir);
        return new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new CountingModuleFactory(workerId),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ipc:Transport"] = "UnixDomainSocket",
                    ["Ipc:UnixSocketDir"] = socketDir,
                    ["Ipc:WorkerId"] = workerId,
                    ["Ipc:HeartbeatIntervalMs"] = "50",
                })
                .Build());
    }

    private static IConfiguration GatewayConfiguration(string socketDir, string workerIds)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ipc:Transport"] = "UnixDomainSocket",
                ["Ipc:UnixSocketDir"] = socketDir,
                ["Ipc:WorkerIds"] = workerIds,
            })
            .Build();
    }

    private static async Task<WebSocket> ConnectAsync(
        MultiWorkerGatewayFactory host,
        string sessionId,
        string clientId,
        CancellationToken cancellationToken)
    {
        var ws = await host.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(host.Server.BaseAddress, "/ws"),
            cancellationToken);
        var hello = Encoding.UTF8.GetBytes(
            $$"""{"type":"hello","sessionId":"{{sessionId}}","clientId":"{{clientId}}","protocolVersion":1}""");
        await ws.SendAsync(hello, WebSocketMessageType.Text, true, cancellationToken);
        return ws;
    }

    private static async Task<WebSocket> ConnectWithRetryAsync(
        MultiWorkerGatewayFactory host,
        string sessionId,
        string clientId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            WebSocket? ws = null;
            try
            {
                ws = await ConnectAsync(host, sessionId, clientId, cancellationToken);
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
                throw new InvalidOperationException("Gateway closed the multi-worker session.");
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

        throw new TimeoutException("no snapshot from the affinity worker within 20s");
    }

    private static async Task WaitGatewayReadyAsync(
        MultiWorkerGatewayFactory host,
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
                throw new TimeoutException("multi-worker gateway never became ready");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class MultiWorkerGatewayFactory : WebApplicationFactory<Chorus.Gateway.Program>
    {
        private readonly string _socketDir;

        public MultiWorkerGatewayFactory(string socketDir)
        {
            _socketDir = socketDir;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ipc:Transport", "UnixDomainSocket");
            builder.UseSetting("Ipc:UnixSocketDir", _socketDir);
            builder.UseSetting("Ipc:WorkerIds", "worker-a,worker-b");
            builder.UseSetting("Faces:StreamerWs:AllowAnonymous", "true");
        }
    }

    private sealed class CountingModule : IGameModule
    {
        private readonly string _workerId;
        private long _applied;

        public CountingModule(string workerId)
        {
            _workerId = workerId;
        }

        public string Name => "multi-worker-counting";

        public object GetStateSnapshot() => new { worker = _workerId, applied = Interlocked.Read(ref _applied) };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
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
        private readonly string _workerId;

        public CountingModuleFactory(string workerId)
        {
            _workerId = workerId;
        }

        public IGameModule Create(string sessionId) => new CountingModule(_workerId);
    }
}
