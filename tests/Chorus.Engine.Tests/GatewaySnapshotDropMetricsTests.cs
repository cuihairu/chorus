using Chorus.Gateway.Core.Faces;

namespace Chorus.Engine.Tests;

/// <summary>
/// Gateway slow-client drops (todo.md:156 ②): the latest-wins eviction has to
/// survive connection churn and reach both /faces and /metrics - otherwise ops
/// cannot tell a bounded drop from an engine publish failure (the pre-fix
/// counter only ever saw post-dispose writes, so it read 0 under churn).
/// </summary>
public sealed class GatewaySnapshotDropMetricsTests
{
    [Fact]
    public void FaceCounters_SnapshotDropped_AppearsInStreamerWsSnapshot()
    {
        var faces = new FaceCounters();
        faces.WsSnapshotDropped();
        faces.WsSnapshotDropped();

        Assert.Equal(2, faces.StreamerWs.SnapshotDropped);
    }

    [Fact]
    public void RenderGateway_ExposesSnapshotDroppedCounter()
    {
        var faces = new FaceCounters();
        for (var i = 0; i < 5; i++)
        {
            faces.WsSnapshotDropped();
        }

        var text = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderGateway(
            faces, ipcPublishFailures: 0, engineSilenceMs: -1);

        Assert.Contains("# TYPE chorus_ws_snapshot_dropped_total counter", text);
        Assert.Contains("chorus_ws_snapshot_dropped_total 5", text);
    }

    [Fact]
    public void RenderGateway_ZeroDrops_StillEmitsCounter()
    {
        var text = Chorus.Gateway.Core.Metrics.ChorusMetricsText.RenderGateway(
            new FaceCounters(), ipcPublishFailures: 0, engineSilenceMs: -1);

        Assert.Contains("chorus_ws_snapshot_dropped_total 0", text);
    }
}
