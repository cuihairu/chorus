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
    public void ServerMetrics_ExposeSessionAndFaceCounters()
    {
        using var factory = new ServerFactory();
        var client = factory.CreateClient();

        PostIngressEvent(client, "metrics-ev-1");

        var metrics = client.GetStringAsync("/metrics").GetAwaiter().GetResult();

        Assert.Contains("chorus_sessions", metrics);
        Assert.Contains("chorus_events_ingested_total", metrics);
        Assert.Contains("chorus_ingress_accepted_total 1", metrics);
        Assert.Contains("chorus_ws_connections_total", metrics);
        Assert.Contains("chorus_tick_errors_total", metrics);
        Assert.Contains("chorus_ipc_publish_failures_total 0", metrics);
        Assert.Contains("chorus_session_start_failures_total 0", metrics);
    }

    [Fact]
    public void ServerMetrics_DuplicateCounted()
    {
        using var factory = new ServerFactory();
        var client = factory.CreateClient();

        PostIngressEvent(client, "metrics-dup-1");
        PostIngressEvent(client, "metrics-dup-1");

        var metrics = client.GetStringAsync("/metrics").GetAwaiter().GetResult();
        Assert.Contains("chorus_ingress_duplicates_total 1", metrics);
    }

    [Fact]
    public void GatewayMetrics_ExposeFacesAndIpcFailures()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var metrics = client.GetStringAsync("/metrics").GetAwaiter().GetResult();

        Assert.Contains("chorus_ingress_accepted_total", metrics);
        Assert.Contains("chorus_ws_acks_dropped_total", metrics);
        Assert.Contains("chorus_ipc_publish_failures_total", metrics);
        Assert.DoesNotContain("chorus_sessions", metrics);
    }

    [Fact]
    public void EngineHostMetrics_ExposeIpcFailuresAndSnapshotBytes()
    {
        using var factory = new EngineHostFactory();
        var client = factory.CreateClient();

        var metrics = client.GetStringAsync("/metrics").GetAwaiter().GetResult();

        Assert.Contains("chorus_sessions", metrics);
        Assert.Contains("chorus_ipc_publish_failures_total", metrics);
        Assert.Contains("chorus_snapshot_bytes", metrics);
        Assert.Contains("chorus_session_start_failures_total", metrics);
    }

    [Fact]
    public void EngineHostSessions_ExposedForSplitModeParity()
    {
        using var factory = new EngineHostFactory();
        var client = factory.CreateClient();

        // The engine is the session-state authority in split mode; /sessions
        // restores the monolith's per-client lag/ack diagnostics.
        var sessions = client.GetStringAsync("/sessions").GetAwaiter().GetResult();

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
    public void Renderer_SessionStartFailures_EmittedOnBothSurfaces()
    {
        // A session refused at start (restore fail-closed / module construction
        // throw) leaves no actor diagnostics, so /sessions cannot show it —
        // this counter is the only way to see such failures.
        var engine = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(
            Array.Empty<Chorus.Engine.Session.SessionDiagnostics>(), ipcPublishFailures: 0, sessionStartFailures: 2);
        Assert.Contains("# TYPE chorus_session_start_failures_total counter", engine);
        Assert.Contains("chorus_session_start_failures_total 2", engine);

        var server = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderServer(
            Array.Empty<Chorus.Engine.Session.SessionDiagnostics>(),
            new Chorus.Gateway.Core.Faces.FaceCounters(),
            ipcPublishFailures: 0,
            sessionStartFailures: 3);
        Assert.Contains("chorus_session_start_failures_total 3", server);
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
