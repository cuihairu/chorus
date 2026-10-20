using System.Net;
using System.Text;
using Chorus.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-3 acceptance: the chorus_* metrics surface is exposed by
/// both hosts and reflects real activity (an accepted ingress event moves
/// the counters).
/// </summary>
public sealed class MetricsEndpointTests
{
    public sealed class ServerFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
        }
    }

    public sealed class GatewayFactory : WebApplicationFactory<global::Chorus.Gateway.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
        }
    }

    public sealed class EngineHostFactory : WebApplicationFactory<global::Chorus.EngineHost.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Random socket dir so concurrent EngineHost hosts never collide on
            // the default /tmp/chorus.
            builder.UseSetting("Ipc:UnixSocketDir", $"/tmp/chorus-test-eh-{Guid.NewGuid():N}");
        }
    }

    [Fact]
    public async Task ServerMetrics_ExposeSessionAndFaceCounters()
    {
        using var factory = new ServerFactory();
        var client = factory.CreateClient();

        PostIngressEvent(client, "metrics-ev-1");

        var metrics = await client.GetStringAsync("/metrics");

        Assert.Contains("chorus_sessions", metrics);
        Assert.Contains("chorus_events_ingested_total", metrics);
        Assert.Contains("chorus_ingress_accepted_total 1", metrics);
        Assert.Contains("chorus_ws_connections_total", metrics);
        Assert.Contains("chorus_tick_errors_total", metrics);
        Assert.Contains("chorus_ipc_publish_failures_total 0", metrics);
        Assert.Contains("chorus_session_start_failures_total 0", metrics);
        Assert.Contains("chorus_session_idle_evictions_total 0", metrics);
        Assert.Contains("chorus_session_capacity_evictions_total 0", metrics);
    }

    [Fact]
    public async Task ServerMetrics_DuplicateCounted()
    {
        using var factory = new ServerFactory();
        var client = factory.CreateClient();

        PostIngressEvent(client, "metrics-dup-1");
        PostIngressEvent(client, "metrics-dup-1");

        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("chorus_ingress_duplicates_total 1", metrics);
    }

    [Fact]
    public async Task GatewayMetrics_ExposeFacesAndIpcFailures()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var metrics = await client.GetStringAsync("/metrics");

        Assert.Contains("chorus_ingress_accepted_total", metrics);
        Assert.Contains("chorus_ws_acks_dropped_total", metrics);
        Assert.Contains("chorus_ipc_publish_failures_total", metrics);
        Assert.DoesNotContain("chorus_sessions", metrics);
    }

    [Fact]
    public async Task EngineHostMetrics_ExposeIpcFailuresAndSnapshotBytes()
    {
        using var factory = new EngineHostFactory();
        var client = factory.CreateClient();

        var metrics = await client.GetStringAsync("/metrics");

        Assert.Contains("chorus_sessions", metrics);
        Assert.Contains("chorus_ipc_publish_failures_total", metrics);
        Assert.Contains("chorus_snapshot_bytes", metrics);
        Assert.Contains("chorus_session_start_failures_total", metrics);
        Assert.Contains("chorus_session_idle_evictions_total", metrics);
        Assert.Contains("chorus_session_capacity_evictions_total", metrics);
    }

    [Fact]
    public async Task EngineHostSessions_ExposedForSplitModeParity()
    {
        using var factory = new EngineHostFactory();
        var client = factory.CreateClient();

        // The engine is the session-state authority in split mode; /sessions
        // restores the monolith's per-client lag/ack diagnostics.
        var sessions = await client.GetStringAsync("/sessions");

        Assert.Equal("[]", sessions.Trim());
    }

    [Fact]
    public void Renderer_AckLagMax_ComputedAcrossSessions()
    {
        var diagnostics = new List<Chorus.Engine.Session.SessionDiagnostics>
        {
            new("s1", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<Chorus.Engine.Session.SessionClientDiagnostics>()),
            new("s2", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new[]
            {
                new Chorus.Engine.Session.SessionClientDiagnostics("c1", 0, 2, 8, 0, 0, -1),
                new Chorus.Engine.Session.SessionClientDiagnostics("c2", 0, 9, 1, 0, 0, -1),
            }),
        };

        var metrics = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(diagnostics, ipcPublishFailures: 0);

        Assert.Contains("chorus_ack_lag_ticks 8", metrics);
        Assert.Contains("chorus_sessions 2", metrics);
    }

    [Fact]
    public void Renderer_SnapshotBytes_MaxAcrossSessions()
    {
        var diagnostics = new List<Chorus.Engine.Session.SessionDiagnostics>
        {
            new("s1", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<Chorus.Engine.Session.SessionClientDiagnostics>(),
                LastSnapshotBytes: 512),
            new("s2", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<Chorus.Engine.Session.SessionClientDiagnostics>(),
                LastSnapshotBytes: 4096),
        };

        var metrics = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(diagnostics, ipcPublishFailures: 0);

        Assert.Contains("# TYPE chorus_snapshot_bytes gauge", metrics);
        Assert.Contains("chorus_snapshot_bytes 4096", metrics);
    }

    [Fact]
    public void Renderer_ClientQueueDropped_SumsAcrossSessions()
    {
        var diagnostics = new List<Chorus.Engine.Session.SessionDiagnostics>
        {
            new("s1", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<Chorus.Engine.Session.SessionClientDiagnostics>(),
                TotalClientQueueDropped: 3),
            new("s2", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                new[] { new Chorus.Engine.Session.SessionClientDiagnostics("c1", 0, 2, 8, 0, 0, -1, ClientQueueDropped: 1) },
                TotalClientQueueDropped: 1),
        };

        var metrics = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(diagnostics, ipcPublishFailures: 0);

        Assert.Contains("# TYPE chorus_client_queue_dropped_total counter", metrics);
        Assert.Contains("chorus_client_queue_dropped_total 4", metrics);
    }

    [Fact]
    public void Renderer_ResyncCounters_SumAcrossSessions()
    {
        // frame-sync P2 counters were collected into SessionDiagnostics (visible
        // via /sessions) but never exported to the Prometheus surface.
        var diagnostics = new List<Chorus.Engine.Session.SessionDiagnostics>
        {
            new("s1", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<Chorus.Engine.Session.SessionClientDiagnostics>(),
                TotalResyncRequests: 5, TotalResyncReplays: 3, TotalResyncWindowMisses: 1, TotalResyncThrottled: 2),
            new("s2", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<Chorus.Engine.Session.SessionClientDiagnostics>(),
                TotalResyncRequests: 4, TotalResyncReplays: 2, TotalResyncWindowMisses: 0, TotalResyncThrottled: 1),
        };

        var metrics = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(diagnostics, ipcPublishFailures: 0);

        Assert.Contains("# TYPE chorus_resync_requests_total counter", metrics);
        Assert.Contains("chorus_resync_requests_total 9", metrics);
        Assert.Contains("chorus_resync_replays_total 5", metrics);
        Assert.Contains("chorus_resync_window_misses_total 1", metrics);
        Assert.Contains("chorus_resync_throttled_total 3", metrics);
    }

    [Fact]
    public void Renderer_FrameHistoryBytes_MaxAcrossSessions()
    {
        var diagnostics = new List<Chorus.Engine.Session.SessionDiagnostics>
        {
            new("s1", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<Chorus.Engine.Session.SessionClientDiagnostics>(),
                FrameHistoryBytes: 1024),
            new("s2", 10, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<Chorus.Engine.Session.SessionClientDiagnostics>(),
                FrameHistoryBytes: 65536),
        };

        var metrics = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(diagnostics, ipcPublishFailures: 0);

        Assert.Contains("# TYPE chorus_frame_history_bytes gauge", metrics);
        Assert.Contains("chorus_frame_history_bytes 65536", metrics);
    }

    [Fact]
    public void Renderer_SessionRegistryCounters_EmittedOnBothSurfaces()
    {
        // Registry-level lifecycle counters: sessions that never produced (or
        // no longer have) actor diagnostics, so /sessions cannot show them.
        var registry = new Chorus.Gateway.Core.Metrics.SessionRegistryCounters(
            StartFailures: 2, IdleEvictions: 5, CapacityEvictions: 1);

        var engine = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(
            Array.Empty<Chorus.Engine.Session.SessionDiagnostics>(), ipcPublishFailures: 0, registry: registry);
        Assert.Contains("# TYPE chorus_session_start_failures_total counter", engine);
        Assert.Contains("chorus_session_start_failures_total 2", engine);
        Assert.Contains("# TYPE chorus_session_idle_evictions_total counter", engine);
        Assert.Contains("chorus_session_idle_evictions_total 5", engine);
        Assert.Contains("chorus_session_capacity_evictions_total 1", engine);

        var server = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderServer(
            Array.Empty<Chorus.Engine.Session.SessionDiagnostics>(),
            new Chorus.Gateway.Core.Faces.FaceCounters(),
            ipcPublishFailures: 0,
            registry: registry);
        Assert.Contains("chorus_session_start_failures_total 2", server);
        Assert.Contains("chorus_session_idle_evictions_total 5", server);
        Assert.Contains("chorus_session_capacity_evictions_total 1", server);
    }

    [Fact]
    public void Renderer_WsResyncDropped_EmittedOnFaceSurfaces()
    {
        // The gateway-side /ws resync fixed-window limiter drops requests before
        // they ever reach the engine, so the engine's chorus_resync_throttled_total
        // cannot see them - this is the gateway-side half of the same signal.
        var faces = new Chorus.Gateway.Core.Faces.FaceCounters();
        for (var i = 0; i < 7; i++)
        {
            faces.WsResyncDropped();
        }

        var server = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderServer(
            Array.Empty<Chorus.Engine.Session.SessionDiagnostics>(),
            faces,
            ipcPublishFailures: 0);
        Assert.Contains("# TYPE chorus_ws_resync_dropped_total counter", server);
        Assert.Contains("chorus_ws_resync_dropped_total 7", server);

        var gateway = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderGateway(
            faces, ipcPublishFailures: 0, engineSilenceMs: 0);
        Assert.Contains("chorus_ws_resync_dropped_total 7", gateway);
    }

    [Fact]
    public void Renderer_Engine_EmitsIpcPublishFailures()
    {
        // The EngineHost surface now reports Engine→Gateway publish failures
        // (snapshots + welcomes), parity with the gateway's counter.
        var metrics = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(
            Array.Empty<Chorus.Engine.Session.SessionDiagnostics>(), ipcPublishFailures: 5);

        Assert.Contains("# TYPE chorus_ipc_publish_failures_total counter", metrics);
        Assert.Contains("chorus_ipc_publish_failures_total 5", metrics);
    }

    private static void PostIngressEvent(HttpClient client, string eventId)
    {
        var response = client.PostAsync("/ingress/test", new StringContent(
            $$"""{"sessionId":"s-metrics","eventId":"{{eventId}}","userId":"u1","kind":"comment","text":"hi"}""",
            Encoding.UTF8,
            "application/json")).GetAwaiter().GetResult();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"ingress returned {response.StatusCode}");
    }
}
