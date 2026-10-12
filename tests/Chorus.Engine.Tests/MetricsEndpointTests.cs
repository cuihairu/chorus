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
    public sealed class ServerFactory : WebApplicationFactory<Program>
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

        var metrics = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderEngine(diagnostics);

        Assert.Contains("chorus_ack_lag_ticks 8", metrics);
        Assert.Contains("chorus_sessions 2", metrics);
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
