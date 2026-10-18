using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Gateway;
using Chorus.Gateway.Core.Faces;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Chorus.Protocol;
using Chorus.Protocol.Messages;
using Chorus.Protocol.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// Split-mode ack / resync loop over the real IPC channel: the gateway's
/// PublishAck/PublishResync must reach the engine's session (per-client ack
/// tracking drives lag detection, and resync is what the chorus_resync_*
/// counters count). Both halves were only exercised through the monolith's
/// in-process path, so a broken gw-&gt;eng ack handler would silently freeze
/// ack state in every split deployment. Also pins the fail-visible edges:
/// publishing before the outgoing bus exists, and a duplicate welcome waiter.
/// </summary>
public sealed class IpcAckResyncLoopTests
{
    private static IConfiguration Config(string socketDir, string? gatewayId = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ipc:Transport"] = "UnixDomainSocket",
                ["Ipc:UnixSocketDir"] = socketDir,
                ["Ipc:HeartbeatIntervalMs"] = "0",
                ["Gateway:Id"] = gatewayId,
            })
            .Build();

    /// <summary>
    /// Full gw->eng ack loop: a real EngineIpcService hosts the engine end of
    /// the UDS channel, a real GatewayIpcService the gateway end. A registered
    /// client acks over IPC and the engine's session must accept it (counted,
    /// and visible in per-client diagnostics).
    /// </summary>
    [Fact]
    public async Task AckOverIpc_IsAcceptedByEngineSession()
    {
        var socketDir = $"/tmp/chorus-test-ack-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1, ResyncRequestCooldownMs = 0 },
            Config(socketDir));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            // The engine hosts the gw->eng server; the gateway role connects to
            // it as a client over the very same endpoint the factory picks.
            var outgoing = new UnixDomainSocketMessageBus(
                Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await ConnectAsync(outgoing, "s-ack", "c-ack", cts.Token);

                var ackTick = await WaitForTickAsync(engine, "s-ack", 2, cts.Token);
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(
                        IpcMessageTypes.GatewayAck, "s-ack",
                        new GatewayAckCmd("s-ack", "c-ack", ackTick, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                    cts.Token);

                var diag = await WaitForAsync(
                    () => engine.GetDiagnostics().FirstOrDefault(d => d.SessionId == "s-ack"),
                    d => d is { TotalAcksAccepted: >= 1 }, cts.Token);

                Assert.NotNull(diag);
                // The ack landed on the client slot the gateway registered, not
                // just on a session-wide counter.
                var client = diag!.Clients.Single(c => c.ClientId == "c-ack");
                Assert.Equal(ackTick, client.LastAckTickId);
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
    /// Stale acks must stay rejected across the IPC boundary (todo §9 阶段一
    /// "ACK 单调性与 Tick 上界"): a second, older ack never rewinds the
    /// accepted tick - it is counted as stale instead.
    /// </summary>
    [Fact]
    public async Task StaleAckOverIpc_CountedStale_AndDoesNotRewind()
    {
        var socketDir = $"/tmp/chorus-test-ackstale-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            Config(socketDir));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(
                Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await ConnectAsync(outgoing, "s-ack-stale", "c-ack-stale", cts.Token);
                var tick = await WaitForTickAsync(engine, "s-ack-stale", 3, cts.Token);

                // Accept the newest tick first...
                await PublishAckAsync(outgoing, "s-ack-stale", "c-ack-stale", tick, cts.Token);
                await WaitForAsync(
                    () => engine.GetDiagnostics().FirstOrDefault(d => d.SessionId == "s-ack-stale"),
                    d => d is { TotalAcksAccepted: >= 1 }, cts.Token);

                // ...then an older one, which must not rewind the slot.
                await PublishAckAsync(outgoing, "s-ack-stale", "c-ack-stale", tick - 1, cts.Token);
                var diag = await WaitForAsync(
                    () => engine.GetDiagnostics().FirstOrDefault(d => d.SessionId == "s-ack-stale"),
                    d => d is { TotalAcksStale: >= 1 }, cts.Token);

                Assert.Equal(tick, diag!.Clients.Single(c => c.ClientId == "c-ack-stale").LastAckTickId);
                Assert.Equal(1, diag.TotalAcksAccepted);
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
    /// Resync over IPC: the engine must count the request and answer it from
    /// the retained frame window (the replay path behind
    /// chorus_resync_requests_total / chorus_resync_replays_total).
    /// </summary>
    [Fact]
    public async Task ResyncOverIpc_ReplaysRetainedWindow()
    {
        var socketDir = $"/tmp/chorus-test-resync-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1, ResyncRequestCooldownMs = 0 },
            Config(socketDir));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(
                Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await ConnectAsync(outgoing, "s-resync", "c-resync", cts.Token);
                var tick = await WaitForTickAsync(engine, "s-resync", 3, cts.Token);

                // From the second-to-last frame: always still inside the retained
                // window, so this is a replay (not a window miss).
                var from = Math.Max(1, tick - 1);
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(
                        IpcMessageTypes.GatewayResync, "s-resync",
                        new GatewayResyncCmd("s-resync", "c-resync", from)),
                    cts.Token);

                var diag = await WaitForAsync(
                    () => engine.GetDiagnostics().FirstOrDefault(d => d.SessionId == "s-resync"),
                    d => d is { TotalResyncRequests: >= 1, TotalResyncReplays: >= 1 }, cts.Token);

                Assert.Equal(1, diag!.TotalResyncRequests);
                Assert.Equal(0, diag.TotalResyncWindowMisses);
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
    /// Ack / resync for a session the engine never created are no-ops: a late
    /// gateway packet must not throw out of the receive loop (which would kill
    /// the IPC handler for every other session).
    /// </summary>
    [Fact]
    public async Task AckAndResync_ForUnknownSession_AreNoOps()
    {
        var socketDir = $"/tmp/chorus-test-unknown-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            Config(socketDir));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(
                Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await WarmUpAsync(outgoing, cts.Token);
                await PublishAckAsync(outgoing, "s-ghost", "c-ghost", 5, cts.Token);
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(
                        IpcMessageTypes.GatewayResync, "s-ghost",
                        new GatewayResyncCmd("s-ghost", "c-ghost", 1)),
                    cts.Token);

                // The engine stays alive and a real session still comes up.
                await ConnectAsync(outgoing, "s-alive", "c-alive", cts.Token);
                var diag = await WaitForAsync(
                    () => engine.GetDiagnostics().FirstOrDefault(d => d.SessionId == "s-alive"),
                    d => d is not null, cts.Token);

                Assert.NotNull(diag);
                Assert.Empty(engine.GetDiagnostics().Where(d => d.SessionId == "s-ghost"));
            }
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------ gateway half

    [Fact]
    public async Task Gateway_PublishAckAndResync_EmitTypedEnvelopes()
    {
        var socketDir = $"/tmp/chorus-test-gwipc-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        // The gateway is the client on gw->eng: host that server here.
        var received = new List<Envelope>();
        var server = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
        server.StartServer();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var collect = Task.Run(async () =>
        {
            await foreach (var e in server.SubscribeAsync(cts.Token))
            {
                lock (received) { received.Add(e); }
            }
        });

        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));
        await gateway.StartAsync(cts.Token);
        try
        {
            // Bring the gateway's outgoing (gw->eng) connection up with an
            // ignored envelope type, so the counted ack/resync publishes below
            // cannot race the listener bind or be retried into duplicates.
            await WarmUpGatewayAsync(gateway, cts.Token);

            await gateway.PublishAckAsync("s-gw", "c-gw", 42, 1234L, cts.Token);
            await gateway.PublishResyncAsync("s-gw", "c-gw", 40, cts.Token);

            await WaitUntilAsync(
                () => { lock (received) { return received.Count >= 2; } },
                cts.Token);

            Envelope ack;
            Envelope resync;
            lock (received)
            {
                ack = received.Single(e => e.Type == IpcMessageTypes.GatewayAck);
                resync = received.Single(e => e.Type == IpcMessageTypes.GatewayResync);
            }

            // Both envelopes must carry the session id so the engine routes
            // them without opening the body first.
            Assert.Equal("s-gw", ack.SessionId);
            Assert.Equal("s-gw", resync.SessionId);

            var ackCmd = EnvelopeFactory.DeserializeBody<GatewayAckCmd>(ack);
            Assert.Equal("s-gw", ackCmd.SessionId);
            Assert.Equal("c-gw", ackCmd.ClientId);
            Assert.Equal(42, ackCmd.LastAppliedTickId);
            Assert.Equal(1234, ackCmd.ClientTimeMs);

            var resyncCmd = EnvelopeFactory.DeserializeBody<GatewayResyncCmd>(resync);
            Assert.Equal("s-gw", resyncCmd.SessionId);
            Assert.Equal("c-gw", resyncCmd.ClientId);
            Assert.Equal(40, resyncCmd.FromFrameId);
        }
        finally
        {
            cts.Cancel();
            await gateway.StopAsync(CancellationToken.None);
            await server.DisposeAsync();
            try { collect.Wait(TimeSpan.FromSeconds(5)); } catch { }
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Gateway_PublishBeforeOutgoingReady_FailsVisible()
    {
        // Not started: the outgoing bus does not exist yet. Must throw loudly
        // rather than swallow the ack (it feeds chorus_ipc_publish_failures_total
        // at the call sites).
        var socketDir = $"/tmp/chorus-test-gwready-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await gateway.PublishAckAsync("s", "c", 1, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await gateway.PublishResyncAsync("s", "c", 1, CancellationToken.None));

        try { Directory.Delete(socketDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Gateway_DuplicateWelcomeWaiter_Throws()
    {
        var socketDir = $"/tmp/chorus-test-gwwait-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = gateway.CreateWelcomeWaiter("s-w", "c-w", cts.Token);

        // Two waiters on one connection would race the welcome: fail visible.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await gateway.CreateWelcomeWaiter("s-w", "c-w", cts.Token));

        try { Directory.Delete(socketDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Gateway_WelcomeWaiter_Cancelled_FailsVisible_AndReleasesKey()
    {
        // 取消的等待必须对 awaiter 抛 OCE，且释放 map 槽位：同一连接 key 之后
        // 还能重新等待（否则一次超时就把该连接的 welcome 永久堵死）。
        var socketDir = $"/tmp/chorus-test-gwcancel-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var waiter = gateway.CreateWelcomeWaiter("s-cx", "c-cx", cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);

        var second = gateway.CreateWelcomeWaiter("s-cx", "c-cx", CancellationToken.None);
        Assert.False(second.IsCompleted);

        try { Directory.Delete(socketDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Malformed control envelopes (undecodable welcome / snapshot bodies) must
    /// be logged and skipped without killing the eng->gw dispatch loop: the next
    /// valid snapshot still fans out to the session's client.
    /// </summary>
    [Fact]
    public async Task Gateway_MalformedControlEnvelopes_DoNotKillDispatchLoop()
    {
        var socketDir = $"/tmp/chorus-test-gwbad-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        // Same topology as the targeted-snapshot test: the gateway hosts the
        // eng->gw listener itself, engineSide is a plain on-demand client.
        var engineSide = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_eng_to_gw.sock"));
        var engineIngress = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
        engineIngress.StartServer();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));

        var target = new RecordingConnection("s-bad", "c-target");
        try
        {
            await gateway.StartAsync(cts.Token);
            await WarmUpGatewayAsync(gateway, cts.Token);
            await WarmUpAsync(engineSide, cts.Token);
            await gateway.RegisterClientAsync(target.Inner, cts.Token);

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Undecodable bodies: a number cannot be a welcome, a wrong-shape
            // object cannot be a snapshot. Both must hit the per-envelope catch.
            await engineSide.PublishAsync(new Envelope(
                Type: IpcMessageTypes.EngineWelcome, MessageId: "m1", SessionId: "s-bad",
                CreatedTimeMs: ts, Body: System.Text.Json.JsonSerializer.SerializeToElement(5)), cts.Token);
            await engineSide.PublishAsync(new Envelope(
                Type: IpcMessageTypes.EngineSnapshot, MessageId: "m2", SessionId: "s-bad",
                CreatedTimeMs: ts, Body: System.Text.Json.JsonSerializer.SerializeToElement(new { wrong = "shape" })), cts.Token);

            // The loop survived both: a valid targeted snapshot still dispatches.
            await engineSide.PublishAsync(
                EnvelopeFactory.Create(IpcMessageTypes.EngineSnapshot, "s-bad",
                    new ServerSnapshot("snapshot", "s-bad", 9, 0, System.Text.Json.JsonSerializer.SerializeToElement(new { v = 1 }), TargetClientId: "c-target")),
                cts.Token);

            await target.WaitForAsync(c => c.TickId == 9, cts.Token);
        }
        finally
        {
            cts.Cancel();
            await gateway.StopAsync(CancellationToken.None);
            await target.DisposeAsync();
            await engineSide.DisposeAsync();
            await engineIngress.DisposeAsync();
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Targeted (forced resync) snapshots must reach only the named client;
    /// an untargeted one fans out to every client of the session.
    /// </summary>
    [Fact]
    public async Task Gateway_TargetedSnapshot_ReachesOnlyThatClient()
    {
        var socketDir = $"/tmp/chorus-test-target-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        // The gateway hosts the eng->gw listener itself; this end is a plain
        // client whose PublishAsync connects on demand. Hosting a competing
        // server here would steal the socket file from the gateway's listener.
        var engineSide = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_eng_to_gw.sock"));
        // The gateway publishes gw.connect on gw->eng; host that end too so
        // RegisterClientAsync completes.
        var engineIngress = new UnixDomainSocketMessageBus(Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
        engineIngress.StartServer();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var gateway = new GatewayIpcService(NullLogger<GatewayIpcService>.Instance, Config(socketDir));

        var target = new RecordingConnection("s-t", "c-target");
        var other = new RecordingConnection("s-t", "c-other");
        try
        {
            await gateway.StartAsync(cts.Token);
            await WarmUpGatewayAsync(gateway, cts.Token);
            // Wait for the gateway's own eng->gw listener to bind before the
            // snapshot publish below; the warm-up envelope is ignored by the
            // gateway's dispatch switch.
            await WarmUpAsync(engineSide, cts.Token);
            await gateway.RegisterClientAsync(target.Inner, cts.Token);
            await gateway.RegisterClientAsync(other.Inner, cts.Token);

            await engineSide.PublishAsync(
                EnvelopeFactory.Create(IpcMessageTypes.EngineSnapshot, "s-t",
                    new ServerSnapshot("snapshot", "s-t", 7, 0, System.Text.Json.JsonSerializer.SerializeToElement(new { v = 1 }), Forced: true, Reason: "lag", TargetClientId: "c-target")),
                cts.Token);

            await target.WaitForAsync(c => c.TickId == 7, cts.Token);

            // Give the untargeted fan-out a fair chance to (wrongly) fire.
            await Task.Delay(200, cts.Token);
            Assert.Empty(other.Sent);
        }
        finally
        {
            cts.Cancel();
            await gateway.StopAsync(CancellationToken.None);
            await target.DisposeAsync();
            await other.DisposeAsync();
            await engineSide.DisposeAsync();
            await engineIngress.DisposeAsync();
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// At capacity, a clientless room remains allocated until explicit policy closes it.
    /// </summary>
    [Fact]
    public async Task DisconnectThenNewSessionAtCap_PreservesClientlessRoomAndRejectsNewRoom()
    {
        var socketDir = $"/tmp/chorus-test-sweep-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1, MaxSessions = 1, IdleEvictAfterMs = 100 },
            Config(socketDir));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(
                Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await ConnectAsync(outgoing, "s-sweep", "c-sweep", cts.Token);
                await WaitForTickAsync(engine, "s-sweep", 1, cts.Token);

                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(IpcMessageTypes.GatewayDisconnect, "s-sweep",
                        new GatewayDisconnectCmd("s-sweep", "c-sweep")), cts.Token);

                await Task.Delay(200, cts.Token);

                await ConnectAsync(outgoing, "s-next", "c-next", cts.Token);
                await Task.Delay(100, cts.Token);
                Assert.Contains(engine.GetDiagnostics(), d => d.SessionId == "s-sweep");
                Assert.DoesNotContain(engine.GetDiagnostics(), d => d.SessionId == "s-next");
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
    /// A gateway disconnect for a live session must clear the per-client
    /// slot (the ack/lag state of a gone client must not linger and drive
    /// lag-driven resyncs for a client that no longer exists).
    /// </summary>
    [Fact]
    public async Task GatewayDisconnect_RemovesClientSlotFromSession()
    {
        var socketDir = $"/tmp/chorus-test-disc-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            Config(socketDir));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(
                Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await ConnectAsync(outgoing, "s-disc", "c-disc", cts.Token);
                await WaitForTickAsync(engine, "s-disc", 1, cts.Token);
                Assert.Single(engine.GetDiagnostics().Single(d => d.SessionId == "s-disc").Clients);

                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(IpcMessageTypes.GatewayDisconnect, "s-disc",
                        new GatewayDisconnectCmd("s-disc", "c-disc")), cts.Token);

                await WaitUntilAsync(
                    () => engine.GetDiagnostics().Single(d => d.SessionId == "s-disc").Clients.Count == 0,
                    cts.Token);
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
    /// A correlation mismatch between the envelope's source event id and the
    /// inner event id is logged, not fatal: the event must still be delivered
    /// (the gateway already acked the platform).
    /// </summary>
    [Fact]
    public async Task GatewayAudienceEvent_SourceEventIdMismatch_LogsButStillDelivers()
    {
        var socketDir = $"/tmp/chorus-test-mis-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new HeadlessFactory(),
            new SessionConfig { TickDurationMs = 10, SnapshotEveryTicks = 1 },
            Config(socketDir));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(
                Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await ConnectAsync(outgoing, "s-mis", "c-mis", cts.Token);
                await WaitForTickAsync(engine, "s-mis", 1, cts.Token);

                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(
                        IpcMessageTypes.GatewayAudienceEvent, "s-mis",
                        new AudienceEvent(
                            EventId: "ev-1", Platform: "test", SessionId: "s-mis", UserId: "u1",
                            Kind: AudienceEventKind.Comment,
                            IngestTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        sourceEventId: "wrong-id"),
                    cts.Token);

                await WaitUntilAsync(
                    () => engine.GetDiagnostics().Single(d => d.SessionId == "s-mis").TotalAppliedEvents >= 1,
                    cts.Token);
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
    /// A full event lane at the engine boundary: the excess is dropped and
    /// counted as overflow - never silently lost. The tick is held mid-apply
    /// (a slow game module) so the bounded lane can actually fill; the drop
    /// counter is published with the next diagnostics snapshot.
    /// </summary>
    [Fact]
    public async Task GatewayAudienceEvent_LaneFull_CountsOverflow()
    {
        var socketDir = $"/tmp/chorus-test-full-{Guid.NewGuid():N}";
        Directory.CreateDirectory(socketDir);
        var applyEntered = new SemaphoreSlim(0, 1);
        var releaseApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new EngineIpcService(
            NullLogger<EngineIpcService>.Instance,
            new BlockingModuleFactory(applyEntered, releaseApply),
            new SessionConfig { TickDurationMs = 10, MaxPendingEvents = 64 },
            Config(socketDir));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await engine.StartAsync(cts.Token);
        try
        {
            var outgoing = new UnixDomainSocketMessageBus(
                Path.Combine(socketDir, "chorus_gw_to_eng.sock"));
            await using (outgoing)
            {
                await ConnectAsync(outgoing, "s-full", "c-full", cts.Token);

                // First event: the actor drains it and enters the slow apply.
                await outgoing.PublishAsync(
                    EnvelopeFactory.Create(
                        IpcMessageTypes.GatewayAudienceEvent, "s-full",
                        new AudienceEvent(
                            EventId: "ev-0", Platform: "test", SessionId: "s-full", UserId: "u1",
                            Kind: AudienceEventKind.Comment, IngestTimeMs: 0)),
                    cts.Token);
                Assert.True(applyEntered.Wait(TimeSpan.FromSeconds(5)), "apply never entered");

                // The tick is parked in apply: the bounded lane fills and the
                // excess is rejected (counted, not lost).
                for (var i = 1; i <= 400; i++)
                {
                    await outgoing.PublishAsync(
                        EnvelopeFactory.Create(
                            IpcMessageTypes.GatewayAudienceEvent, "s-full",
                            new AudienceEvent(
                                EventId: $"ev-{i}", Platform: "test", SessionId: "s-full", UserId: "u1",
                                Kind: AudienceEventKind.Comment, IngestTimeMs: 0)),
                        cts.Token);
                }

                releaseApply.SetResult();

                var diag = await WaitForAsync(
                    () => engine.GetDiagnostics().FirstOrDefault(d => d.SessionId == "s-full"),
                    d => d is { TotalDroppedOverflow: >= 1 }, cts.Token);

                Assert.True(diag!.TotalDroppedOverflow >= 1);
                Assert.True(diag.TotalIngestedEvents > 0);
            }
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync(CancellationToken.None);
            try { Directory.Delete(socketDir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task ConnectAsync(IMessageBus outgoing, string sessionId, string clientId, CancellationToken ct)
    {
        await WarmUpAsync(outgoing, ct);
        await outgoing.PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayConnect, sessionId, new GatewayConnectCmd(sessionId, clientId)),
            ct);
    }

    /// <summary>
    /// Both sides bind their UDS listener inside a background retry loop, so a
    /// publish issued right after StartAsync can hit a not-yet-created socket
    /// file. Establish the client connection first with a throwaway envelope
    /// the receiver's switch ignores, so every counted publish below runs on a
    /// live channel (no blind retry, which could double-count).
    /// </summary>
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

    private static ValueTask PublishAckAsync(IMessageBus outgoing, string sessionId, string clientId, long tickId, CancellationToken ct)
        => outgoing.PublishAsync(
            EnvelopeFactory.Create(IpcMessageTypes.GatewayAck, sessionId,
                new GatewayAckCmd(sessionId, clientId, tickId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
            ct);

    private static async Task<long> WaitForTickAsync(EngineIpcService engine, string sessionId, long minTick, CancellationToken ct)
    {
        var diag = await WaitForAsync(
            () => engine.GetDiagnostics().FirstOrDefault(d => d.SessionId == sessionId),
            d => d is not null && d.TickId >= minTick, ct);
        return diag!.TickId;
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, Func<T?, bool> ready, CancellationToken ct) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var value = probe();
            if (ready(value))
            {
                return value;
            }

            if (sw.ElapsedMilliseconds > 20_000 || ct.IsCancellationRequested)
            {
                throw new TimeoutException("engine never reached the expected state");
            }

            await Task.Delay(10, ct);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 20_000 || ct.IsCancellationRequested)
            {
                throw new TimeoutException("expected envelope never arrived");
            }

            await Task.Delay(10, ct);
        }
    }

    /// <summary>
    /// Warm the gateway's own outgoing bus with an ignored envelope type so the
    /// first counted publish cannot race the engine-side listener bind.
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
                // StartAsync only schedules ExecuteAsync, so the outgoing bus is
                // still null on the very first publish (the pre-bind window).
                await Task.Delay(20, ct);
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

    private sealed class HeadlessFactory : IGameModuleFactory
    {
        public IGameModule Create(string sessionId) => new HeadlessModule();
    }

    private sealed class HeadlessModule : IGameModule
    {
        public string Name => "headless-ipc";
        public object GetStateSnapshot() => new { };
        public void ApplyEvents(long tickId, IReadOnlyList<Chorus.Protocol.Models.AudienceEvent> events) { }
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }
        public void Dispose() { }
    }

    /// <summary>Module whose first apply parks until released: holds the tick
    /// mid-apply so a bounded event lane can fill behind it.</summary>
    private sealed class BlockingModuleFactory : IGameModuleFactory
    {
        private readonly SemaphoreSlim _applyEntered;
        private readonly TaskCompletionSource _releaseApply;

        public BlockingModuleFactory(SemaphoreSlim applyEntered, TaskCompletionSource releaseApply)
        {
            _applyEntered = applyEntered;
            _releaseApply = releaseApply;
        }

        public IGameModule Create(string sessionId) => new BlockingModule(_applyEntered, _releaseApply);
    }

    private sealed class BlockingModule : IGameModule
    {
        private readonly SemaphoreSlim _applyEntered;
        private readonly TaskCompletionSource _releaseApply;

        public BlockingModule(SemaphoreSlim applyEntered, TaskCompletionSource releaseApply)
        {
            _applyEntered = applyEntered;
            _releaseApply = releaseApply;
        }

        public string Name => "blocking-ipc";
        public object GetStateSnapshot() => new { };
        public void ApplyEvents(long tickId, IReadOnlyList<Chorus.Protocol.Models.AudienceEvent> events)
        {
            _applyEntered.Release();
            _releaseApply.Task.GetAwaiter().GetResult();
        }
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }
        public void Dispose() { }
    }

    /// <summary>
    /// GatewayClientConnection over a stub socket that records payloads. The
    /// production type is sealed, so the recorder composes it instead of
    /// deriving from it.
    /// </summary>
    private sealed class RecordingConnection : IAsyncDisposable
    {
        private readonly List<ServerSnapshot> _sent = new();
        private readonly SemaphoreSlim _gate = new(1, 1);

        public RecordingConnection(string sessionId, string clientId)
        {
            Inner = new GatewayClientConnection(sessionId, clientId, new RecordingSocket(_sent, _gate), new FaceCounters());
        }

        public GatewayClientConnection Inner { get; }

        public IReadOnlyList<ServerSnapshot> Sent
        {
            get { _gate.Wait(); try { return _sent.ToList(); } finally { _gate.Release(); } }
        }

        public async Task WaitForAsync(Func<ServerSnapshot, bool> predicate, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20_000)
            {
                if (Sent.Any(predicate))
                {
                    return;
                }

                await Task.Delay(10, ct);
            }

            throw new TimeoutException("snapshot never arrived on this connection");
        }

        public async ValueTask DisposeAsync()
        {
            await Inner.DisposeAsync();
            _gate.Dispose();
        }
    }

    private sealed class RecordingSocket : WebSocket
    {
        private readonly List<ServerSnapshot> _sent;
        private readonly SemaphoreSlim _gate;

        public RecordingSocket(List<ServerSnapshot> sent, SemaphoreSlim gate)
        {
            _sent = sent;
            _gate = gate;
        }

        public override WebSocketState State => WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override void Abort() => throw new NotSupportedException();

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override void Dispose()
        {
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            // The wire format is the WS protocol's camelCase JSON; decode with
            // the same options or TickId and friends stay at their defaults.
            var doc = System.Text.Json.JsonSerializer.Deserialize<ServerSnapshot>(
                (ReadOnlySpan<byte>)buffer.AsSpan(), ProtocolJson.SerializerOptions);
            _gate.Wait();
            try
            {
                _sent.Add(doc!);
            }
            finally
            {
                _gate.Release();
            }

            return Task.CompletedTask;
        }
    }
}
