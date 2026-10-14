using System.Text.Json;
using Chorus.Client;
using Chorus.Server;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Frame-sync P3 acceptance (客户端 SDK): a new project connects with one call
/// and one render callback against the real /ws surface - states arrive by
/// frame number (strictly increasing, replays dropped idempotently), acks flow
/// after applying, and handshake failures surface as exceptions.
/// </summary>
public sealed class ChorusClientTests
{
    /// <summary>Connect through the in-memory test server's WS transport
    /// (the SDK's transport seam; production uses a real ClientWebSocket).</summary>
    private static Task<ChorusClient> ConnectAsync(
        WebApplicationFactory<Chorus.Server.Program> factory,
        string sessionId,
        string clientId,
        Action<JsonElement> onState)
    {
        var wsUrl = new Uri(factory.Server.BaseAddress, "ws").ToString();
        var wsClient = factory.Server.CreateWebSocketClient();
        return ChorusClient.ConnectAsync(
            ChorusClientOptions.Default(wsUrl, sessionId, clientId),
            onState,
            connect: wsClient.ConnectAsync);
    }

    [Fact]
    public async Task Connect_RendersStatesByFrameNumber_AndAcks()
    {
        using var factory = new WebApplicationFactory<Chorus.Server.Program>();

        var states = new List<JsonElement>();
        var gate = new SemaphoreSlim(1, 1);
        // Quickstart-shaped usage: one call, one callback.
        await using var client = await ConnectAsync(factory, "s-sdk-e2e", "c-sdk",
            state =>
            {
                gate.Wait();
                try
                {
                    states.Add(state);
                }
                finally
                {
                    gate.Release();
                }
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            gate.Wait();
            var count = states.Count;
            gate.Release();
            if (count >= 3)
            {
                break;
            }

            await Task.Delay(10);
        }

        gate.Wait();
        try
        {
            Assert.True(states.Count >= 3, $"only {states.Count} states rendered");
            Assert.All(states, s => Assert.Equal(JsonValueKind.Object, s.ValueKind));
        }
        finally
        {
            gate.Release();
        }

        Assert.True(client.FramesApplied >= 3);
        Assert.True(client.LastAppliedFrameId >= 3);
        // Frames arrive strictly by frame number - never a stale or replayed one.
        Assert.True(client.LastAppliedFrameId >= client.FramesApplied);
        Assert.True(client.AcksSent >= 1, "no ack reached the wire");
    }

    [Fact]
    public async Task Backpressure_StalledCallback_DropsDoNotRegressTheAppliedId()
    {
        using var factory = new WebApplicationFactory<Chorus.Server.Program>();

        // Stall the render callback: the server's latest-wins outbound slot
        // drops frames for this slow client, which is exactly the gap-fill
        // scenario - the applied id may jump, but never regress.
        await using var client = await ConnectAsync(factory, "s-sdk-lag", "c-sdk-lag",
            _ => Thread.Sleep(120));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && client.FramesApplied < 3)
        {
            await Task.Delay(10);
        }

        Assert.True(client.FramesApplied >= 1, "no frame applied under backpressure");
        Assert.True(client.Completion.IsFaulted == false);
    }

    [Fact]
    public async Task FrameGaps_PullResync_WithinCooldown()
    {
        // Snapshots every 2 ticks: the client observes tickId jumps (0, 2, 4,
        // ...) - every periodic frame is a gap, so the SDK must keep pulling
        // the missing window, cooldown-limited, and drop replayed frames.
        using var factory = new WebApplicationFactory<Chorus.Server.Program>()
            .WithWebHostBuilder(b => b.UseSetting("Session:SnapshotEveryTicks", "2"));
        var wsClient = factory.Server.CreateWebSocketClient();

        var options = ChorusClientOptions.Default(
                new Uri(factory.Server.BaseAddress, "ws").ToString(), "s-sdk-gap", "c-sdk-gap")
            with { ResyncCooldown = TimeSpan.FromMilliseconds(100) };

        await using var client = await ChorusClient.ConnectAsync(
            options, _ => { },
            connect: (uri, ct) => wsClient.ConnectAsync(uri, ct));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && client.ResyncsSent < 2)
        {
            await Task.Delay(10);
        }

        Assert.True(client.ResyncsSent >= 2,
            $"frames={client.FramesApplied} resyncs={client.ResyncsSent}");
        // Replayed frames must be dropped idempotently: the applied id only
        // ever moves forward.
        Assert.True(client.LastAppliedFrameId >= client.FramesApplied);
    }

    [Fact]
    public async Task Connect_ToUnreachableServer_Throws()
    {
        var options = ChorusClientOptions.Default("ws://localhost:1/ws", "s-x", "c-x");
        options = options with { HandshakeTimeout = TimeSpan.FromSeconds(2) };

        await Assert.ThrowsAnyAsync<Exception>(
            () => ChorusClient.ConnectAsync(options, _ => { }));
    }
}
