using System.Diagnostics;
using System.Net.Sockets;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Chorus.Protocol.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// Event-path survival under a stalled eng-&gt;gw bus. The engine's receive
/// loop serves every gw-&gt;eng envelope on one thread, and a GatewayConnect
/// used to inline-await its welcome publish over the eng-&gt;gw bus. When the
/// gateway side cannot drain (2026-10-15 gate run, 800 rooms x reconnect
/// churn: 21.5k eng-&gt;gw messages/s, one socket, one lock), that publish
/// blocks inside the kernel send buffer and the event path freezes with it -
/// audience events stopped being ingested while connects kept flowing.
/// Welcomes now go through a bounded queue off the receive loop: a stalled
/// bus must show up as welcome publish failures, never as frozen ingestion.
/// </summary>
public sealed class EngineWelcomeDeliveryTests
{
    private const int Connects = 20_000;
    private const string SessionId = "s-churn";

    [Fact]
    public async Task StalledEngToGatewayBus_DoesNotFreezeEventIngestion()
    {
        var socketDir = $"/tmp/chorus-test-welcome-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessFactory(),
            new SessionConfig
            {
                MaxSessions = 0,
                IdleEvictAfterMs = 1_000_000,
                TickDurationMs = 10_000,
                SnapshotEveryTicks = 1,
                ResyncRequestCooldownMs = 0,
            },
            Config(socketDir));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // A gateway-side listener that accepts the engine's outgoing connection
        // and never reads: every eng->gw frame piles up in the kernel buffer
        // until the writer blocks. Listening before the engine starts, so the
        // engine's lazy client connect always lands here (backlog is fine -
        // accept only needs to happen once).
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(Path.Combine(socketDir, "chorus_eng_to_gw.sock")));
        listener.Listen(8);
        var connected = Task.Run(() => listener.Accept());

        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await WarmUpAsync(outgoing, cts.Token);

                // The engine's outgoing client connects lazily on its first
                // publish, which is the welcome of the first connect: seed one,
                // then hold that connection open and never read from it.
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(IpcMessageTypes.GatewayConnect, SessionId,
                        new GatewayConnectCmd(SessionId, "c-0")), cts.Token);
                var gatewaySocket = await connected.WaitAsync(TimeSpan.FromSeconds(20));

                for (var i = 1; i < Connects; i++)
                {
                    await outgoing.PublishAsync(
                        EnvelopeFactory.Create(IpcMessageTypes.GatewayConnect, SessionId,
                            new GatewayConnectCmd(SessionId, $"c-{i}")), cts.Token);
                }

                // One audience event behind the connect storm. With the old
                // inline welcome publish the receive loop is wedged behind the
                // stalled bus and this never gets ingested; with the welcome
                // queue it must arrive regardless.
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(IpcMessageTypes.GatewayAudienceEvent, SessionId,
                        new AudienceEvent("e-1", "test", SessionId, "u", AudienceEventKind.Like, 1)), cts.Token);

                await WaitUntilAsync(() => engine.GetDiagnostics()
                    .FirstOrDefault(d => d.SessionId == SessionId)?.TotalIngestedEvents >= 1, cts.Token);

                // The stall was real, and it cost welcomes rather than events:
                // at least one welcome was dropped or failed to publish.
                Assert.True(engine.PublishFailures > 0,
                    "expected welcome publish failures against a stalled eng->gw bus");

                var diag = engine.GetDiagnostics()
                    .Single(d => d.SessionId == SessionId);
                Assert.Equal(0, diag.TotalTickErrors);

                gatewaySocket.Dispose();
            }
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync(CancellationToken.None);
            listener.Dispose();
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

    /// <summary>Both sides bind their UDS listener inside a background retry
    /// loop, so a publish issued right after StartAsync can hit a not-yet-
    /// created socket file. Establish the client connection first with a
    /// throwaway envelope the receiver's switch ignores.</summary>
    private static async Task WarmUpAsync(IMessageBus outgoing, CancellationToken ct)
    {
        var probe = EnvelopeFactory.Create(IpcMessageTypes.EngineHeartbeat, "__warmup", new { });
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await outgoing.PublishAsync(probe, ct);
                return;
            }
            catch (IOException) when (sw.ElapsedMilliseconds < 20_000)
            {
                await Task.Delay(20, ct);
            }
            catch (SocketException) when (sw.ElapsedMilliseconds < 20_000)
            {
                await Task.Delay(20, ct);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 30_000 || ct.IsCancellationRequested)
            {
                throw new TimeoutException("expected condition never arrived");
            }

            await Task.Delay(10, ct);
        }
    }

    private sealed class HeadlessFactory : IGameModuleFactory
    {
        public IGameModule Create(string sessionId) => new HeadlessModule();
    }

    private sealed class HeadlessModule : IGameModule
    {
        public string Name => "headless-welcome";
        public object GetStateSnapshot() => new { };
        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events) { }
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }
        public void Dispose() { }
    }
}
