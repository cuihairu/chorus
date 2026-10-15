using System.Diagnostics;
using System.Net.Sockets;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Chorus.Protocol.Messages;
using Chorus.Protocol.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// Engine-side sweep behavior over the real IPC channel: the receive loop's
/// amortized sweep must (a) reclaim a client-less session at the session cap
/// within the tightened 1s cadence — not 30s, and not by re-sweeping on every
/// message — and (b) keep a session alive while ANY of its clients stays
/// connected (per-client counting, not per-session).
/// </summary>
public sealed class EngineSweepTests
{
    private static IConfiguration Config(string socketDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ipc:Transport"] = "UnixDomainSocket",
                ["Ipc:UnixSocketDir"] = socketDir,
                ["Ipc:HeartbeatIntervalMs"] = "0",
            })
            .Build();

    private static EngineIpcService HostEngine(string socketDir, int maxSessions) => new(
        NullLogger<EngineIpcService>.Instance,
        new HeadlessFactory(),
        new SessionConfig
        {
            MaxSessions = maxSessions,
            IdleEvictAfterMs = 100,
            TickDurationMs = 10,
            SnapshotEveryTicks = 1,
            ResyncRequestCooldownMs = 0,
        },
        Config(socketDir));

    [Fact]
    public async Task AtCapacity_ClientlessIdleSession_ReclaimedWithinFastCadence()
    {
        var socketDir = $"/tmp/chorus-test-sweep-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = HostEngine(socketDir, maxSessions: 2);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await ConnectAsync(outgoing, "s-keep", "c-keep", cts.Token);
                await ConnectAsync(outgoing, "s-evict", "c-evict", cts.Token);
                await WaitUntilAsync(
                    () => engine.GetDiagnostics().Any(d => d.SessionId == "s-keep")
                        && engine.GetDiagnostics().Any(d => d.SessionId == "s-evict"), cts.Token);

                // The only client of s-evict walks away; s-keep stays connected.
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(IpcMessageTypes.GatewayDisconnect, "s-evict",
                        new GatewayDisconnectCmd("s-evict", "c-evict")), cts.Token);

                // Keep message pressure on the receive loop (audience events to
                // the surviving session): at capacity the sweep gate must open
                // within 1s of the last sweep and reclaim the dead stream. The
                // idle window is 100ms, so within the wait budget the eviction
                // must happen — 30s idle cadence would time this test out.
                var pressure = Task.Run(async () =>
                {
                    var n = 0;
                    while (!cts.IsCancellationRequested && engine.IdleEvictions == 0)
                    {
                        await outgoing.PublishAsync(
                            EnvelopeFactory.Create(IpcMessageTypes.GatewayAudienceEvent, "s-keep",
                                new AudienceEvent($"e-{n++}", "test", "s-keep", "u", AudienceEventKind.Like, 1)), cts.Token);
                        await Task.Delay(100, cts.Token);
                    }
                }, cts.Token);

                await WaitUntilAsync(() => engine.IdleEvictions >= 1, cts.Token);
                await pressure;

                Assert.Contains(engine.GetDiagnostics(), d => d.SessionId == "s-keep");
                Assert.DoesNotContain(engine.GetDiagnostics(), d => d.SessionId == "s-evict");
            }
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemainingClient_KeepsSessionAliveThroughSweeps()
    {
        var socketDir = $"/tmp/chorus-test-sweep2-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = HostEngine(socketDir, maxSessions: 1);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                // Two clients on the single session (also the capacity).
                await ConnectAsync(outgoing, "s-live", "c-1", cts.Token);
                await ConnectAsync(outgoing, "s-live", "c-2", cts.Token);
                await WaitUntilAsync(
                    () => engine.GetDiagnostics().Any(d => d.SessionId == "s-live"), cts.Token);

                // One client drops: the session still has a client and must
                // survive every sweep the capacity cadence fires (per-client
                // counting — clearing the whole session on any disconnect
                // would evict a live stream).
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(IpcMessageTypes.GatewayDisconnect, "s-live",
                        new GatewayDisconnectCmd("s-live", "c-1")), cts.Token);

                // Event pressure at capacity for far longer than idle window +
                // cadence: IdleEvictions must stay 0 the whole time.
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 3_000)
                {
                    await outgoing.PublishAsync(
                        EnvelopeFactory.Create(IpcMessageTypes.GatewayAudienceEvent, "s-live",
                            new AudienceEvent($"e-{sw.ElapsedMilliseconds}", "test", "s-live", "u", AudienceEventKind.Like, 1)), cts.Token);
                    await Task.Delay(100, cts.Token);
                }

                Assert.Equal(0, engine.IdleEvictions);
                Assert.Contains(engine.GetDiagnostics(), d => d.SessionId == "s-live");

                // The last client drops too: now the sweep must take it.
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(IpcMessageTypes.GatewayDisconnect, "s-live",
                        new GatewayDisconnectCmd("s-live", "c-2")), cts.Token);

                var pressure = Task.Run(async () =>
                {
                    var n = 0;
                    while (!cts.IsCancellationRequested && engine.IdleEvictions == 0)
                    {
                        // Events for the gone session re-create it on arrival
                        // (GetOrCreate), so drive the sweep through connect
                        // churn instead: a fresh registration is also a
                        // receive-loop message and keeps the cadence ticking.
                        await outgoing.PublishAsync(
                            EnvelopeFactory.Create(IpcMessageTypes.GatewayConnect, "s-next",
                                new GatewayConnectCmd("s-next", "c-next")), cts.Token);
                        await outgoing.PublishAsync(
                            EnvelopeFactory.Create(IpcMessageTypes.GatewayDisconnect, "s-next",
                                new GatewayDisconnectCmd("s-next", "c-next")), cts.Token);
                        n++;
                        await Task.Delay(100, cts.Token);
                    }
                }, cts.Token);

                await WaitUntilAsync(() => engine.IdleEvictions >= 1, cts.Token);
                await pressure;
            }
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    private static async Task ConnectAsync(IMessageBus outgoing, string sessionId, string clientId, CancellationToken ct)
    {
        await WarmUpAsync(outgoing, ct);
        await outgoing.PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayConnect, sessionId, new GatewayConnectCmd(sessionId, clientId)),
            ct);
    }

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
            if (sw.ElapsedMilliseconds > 20_000 || ct.IsCancellationRequested)
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
        public string Name => "headless-sweep";
        public object GetStateSnapshot() => new { };
        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events) { }
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }
        public void Dispose() { }
    }
}
