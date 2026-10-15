using Chorus.Adapters.Platforms;
using Chorus.Engine.Abstractions;
using Chorus.Gateway;
using Chorus.Gateway.Core.Actors;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Chorus.Protocol.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// The gateway-side actor ref over IPC: TellAsync must surface engine
/// unavailability as <see cref="SessionTellOutcome.Unavailable"/> (covered
/// end-to-end by GatewayIngressTellTests), but a cancelled Tell is not an
/// engine failure — it rethrows so the caller's own cancellation semantics
/// survive (previously uncovered).
/// </summary>
public sealed class IpcSessionActorRouterTests
{
    [Fact]
    public async Task TellAsync_CanceledToken_RethrowsOperationCanceled()
    {
        var socketDir = $"/tmp/chorus-test-actorref-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        // Host the engine end of gw->eng so the warm-up (and therefore the
        // outgoing bus) can come up; the welcome/listener itself is unused.
        var engineIngress = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
        engineIngress.StartServer();
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));
        var actor = new IpcSessionActorRef(gateway, "s-actor");
        var evt = new AudienceEvent(
            EventId: "ev-a", Platform: PlatformNames.Douyin, SessionId: "s-actor", UserId: "u1",
            Kind: AudienceEventKind.Comment, IngestTimeMs: 0, Text: "hi");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try
        {
            await gateway.StartAsync(CancellationToken.None);
            await WarmUpGatewayAsync(gateway, CancellationToken.None);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await actor.TellAsync(evt, cts.Token));
            Assert.Equal(cts.Token, ex.CancellationToken);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            await engineIngress.DisposeAsync();
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ActorRef_ExposesSessionId()
    {
        var socketDir = $"/tmp/chorus-test-actorref2-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));

        var actor = new IpcSessionActorRef(gateway, "s-actor-2");

        Assert.Equal("s-actor-2", actor.SessionId);
        // No in-process actor behind the IPC ref, so there is no locally
        // visible tick id.
        Assert.Null(actor.TickId);
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

    private static async Task WarmUpGatewayAsync(GatewayIpcService gateway, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
}
