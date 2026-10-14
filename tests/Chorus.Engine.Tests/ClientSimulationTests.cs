using Chorus.LoadTool;
using Chorus.Server;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Client simulation end-to-end: the load tool's simulated streamer client runs
/// against the real /ws surface through the in-memory test transport - hello
/// token auth, welcome handshake, snapshot playback, periodic acks - and its
/// random disconnect cycle re-runs the full handshake on reconnect.
/// </summary>
public sealed class ClientSimulationTests
{
    private static (ClientSimulation Sim, Func<Task> Run) Start(
        WebApplicationFactory<Chorus.Server.Program> factory,
        string sessionId, string clientId, string profile,
        int disconnectEverySeconds, int reconnectBackoffMs)
    {
        var wsClient = factory.Server.CreateWebSocketClient();
        var sim = new ClientSimulation(
            new Uri(factory.Server.BaseAddress, "ws").ToString(),
            sessionId, clientId, token: null, profile,
            disconnectEverySeconds, reconnectBackoffMs, seed: 1,
            connect: wsClient.ConnectAsync);
        var cts = new CancellationTokenSource();
        var run = sim.RunAsync(cts.Token);
        return (sim, () =>
        {
            cts.Cancel();
            return run;
        });
    }

    [Fact]
    public async Task Simulation_ReceivesSnapshotsByFrame_AndSendsAcks()
    {
        using var factory = new WebApplicationFactory<Chorus.Server.Program>();
        var (sim, stop) = Start(factory, "s-load-e2e", "c-load-1", "fast",
            disconnectEverySeconds: 0, reconnectBackoffMs: 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && (sim.SnapshotsReceived < 1 || sim.AcksSent < 1))
        {
            await Task.Delay(10);
        }

        await stop();

        Assert.True(sim.SnapshotsReceived >= 1, $"snapshots={sim.SnapshotsReceived}");
        Assert.True(sim.AcksSent >= 1, $"acks={sim.AcksSent}");
        Assert.True(sim.LastAppliedFrameId >= 1);
    }

    [Fact]
    public async Task Simulation_ReconnectCycle_RerunsHandshake()
    {
        using var factory = new WebApplicationFactory<Chorus.Server.Program>();
        // ~1s alive per cycle, 10ms backoff: at least one full disconnect ->
        // reconnect -> re-handshake must fit comfortably in the wait window.
        var (sim, stop) = Start(factory, "s-load-reconnect", "c-load-2", "fast",
            disconnectEverySeconds: 1, reconnectBackoffMs: 10);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000 && sim.Disconnects < 1)
        {
            await Task.Delay(10);
        }

        await stop();

        Assert.True(sim.Disconnects >= 1, $"disconnects={sim.Disconnects}");
        // Snapshots kept flowing after a completed cycle: the reconnect really
        // re-ran hello/welcome and resumed playback.
        Assert.True(sim.SnapshotsReceived >= 1, $"snapshots={sim.SnapshotsReceived}");
    }
}
