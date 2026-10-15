using System.Net.WebSockets;
using System.Text;
using System.Diagnostics;
using Chorus.Gateway;
using Chorus.Gateway.Core.Faces;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// IpcSessionRouter fail-closed branches: a failed engine registration and a
/// never-arriving welcome must both resolve to an error connection whose
/// socket carries a visible ServerError — never a silent half-connection or
/// a hang. (Both catch arms were previously uncovered.)
/// </summary>
public sealed class IpcSessionRouterTests
{
    [Fact]
    public async Task ConnectAsync_RegisterFails_SendsEngineUnavailable()
    {
        var socketDir = $"/tmp/chorus-test-router-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        // Never started: the outgoing bus does not exist, so RegisterClientAsync
        // throws deterministically and the router must fail visible.
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));
        var ws = new ErrorRecordingSocket();
        try
        {
            var router = new IpcSessionRouter(gateway);

            var conn = await router.ConnectAsync("s-r", "c-r", ws, CancellationToken.None);

            Assert.Null(conn.Welcome);
            Assert.NotNull(conn.Binding);
            var sent = Encoding.UTF8.GetString(Assert.Single(ws.Sent));
            Assert.Contains("Engine unavailable", sent);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ConnectAsync_WelcomeNeverArrives_TimesOutWithVisibleError()
    {
        var socketDir = $"/tmp/chorus-test-router2-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        // Host the engine end of gw->eng so registration completes, but never
        // deliver a welcome: the 2s welcome timeout must fire and fail visible.
        var engineIngress = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
        engineIngress.StartServer();
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));
        var ws = new ErrorRecordingSocket();
        try
        {
            await gateway.StartAsync(CancellationToken.None);
            await WarmUpGatewayAsync(gateway, CancellationToken.None);
            var router = new IpcSessionRouter(gateway);

            var conn = await router.ConnectAsync("s-r2", "c-r2", ws, CancellationToken.None);

            Assert.Null(conn.Welcome);
            Assert.NotNull(conn.Binding);
            var sent = Encoding.UTF8.GetString(Assert.Single(ws.Sent));
            Assert.Contains("Engine welcome timeout", sent);

            // The binding still forwards ack/resync over IPC after the welcome
            // timed out — both must succeed on the live outgoing bus.
            await conn.Binding.AckAsync(42, 1234L, CancellationToken.None);
            await conn.Binding.ResyncAsync(40, CancellationToken.None);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            await engineIngress.DisposeAsync();
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    private static IConfiguration Config(string socketDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ipc:Transport"] = "UnixDomainSocket",
                ["Ipc:UnixSocketDir"] = socketDir,
                ["Ipc:HeartbeatIntervalMs"] = "0",
            })
            .Build();

    /// <summary>
    /// StartAsync only schedules ExecuteAsync, so the first publish can outrun
    /// the outgoing bus (pre-bind window). Retry a throwaway envelope until it
    /// goes through; the receiver ignores unknown envelope types.
    /// </summary>
    private static async Task WarmUpGatewayAsync(GatewayIpcService gateway, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await gateway.PublishAudienceEventAsync(
                    EnvelopeFactory.Create(IpcMessageTypes.EngineHeartbeat, "__warmup", new { }), ct);
                return;
            }
            catch (InvalidOperationException) when (sw.ElapsedMilliseconds < 20_000)
            {
                await Task.Delay(20, ct);
            }
            catch (IOException) when (sw.ElapsedMilliseconds < 20_000)
            {
                await Task.Delay(20, ct);
            }
            catch (System.Net.Sockets.SocketException) when (sw.ElapsedMilliseconds < 20_000)
            {
                await Task.Delay(20, ct);
            }
        }
    }

    private sealed class ErrorRecordingSocket : WebSocket
    {
        public List<byte[]> Sent { get; } = new();

        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort() => throw new NotSupportedException();

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override void Dispose()
        {
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var copy = new byte[buffer.Count];
            Buffer.BlockCopy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
            Sent.Add(copy);
            return Task.CompletedTask;
        }
    }
}
