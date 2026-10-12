using System.Diagnostics;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-3 acceptance (IPC 断连/重启/回退): after a server restart the
/// client's lazy reconnect delivers on the next publish; a publish while the
/// server is down fails loudly (becoming chorus_ipc_publish_failures_total at
/// the call sites, never a silent drop); Auto picks UDS on Linux and falls
/// back to NamedPipe when the UDS path cannot be bound.
/// </summary>
public sealed class IpcResilienceTests : IAsyncDisposable
{
    private readonly string _socketPath = $"/tmp/chorus-test-{Guid.NewGuid():N}.sock";
    private readonly List<IMessageBus> _buses = new();

    [Fact]
    public async Task ServerRestart_ClientRecoversOnNextPublish()
    {
        var cts = new CancellationTokenSource();
        var server1 = CreateServer();
        var received1 = CollectAsync(server1, cts.Token);

        var client = new UnixDomainSocketMessageBus(_socketPath);
        _buses.Add(client);

        await client.PublishAsync(Env("m1"), cts.Token);
        await WaitUntilAsync(() => received1.Any(e => e.MessageId == "m1"));

        // Server goes down: the next publish must fail loudly, not vanish.
        await server1.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.PublishAsync(Env("m2"), cts.Token));

        // Server restarts on the same path: recovery without client rebuild.
        var server2 = CreateServer();
        var received2 = CollectAsync(server2, cts.Token);

        await client.PublishAsync(Env("m3"), cts.Token);
        await WaitUntilAsync(() => received2.Any(e => e.MessageId == "m3"));

        cts.Cancel();
    }

    [Fact]
    public void Auto_SelectsUdsOnLinux()
    {
        if (!System.Net.Sockets.Socket.OSSupportsUnixDomainSockets)
        {
            return;
        }

        var endpoint = new IpcEndpoint(
            UnixSocketPath: _socketPath,
            NamedPipeName: "chorus-test-pipe",
            TcpHost: "127.0.0.1",
            TcpPort: 0,
            HttpUrl: string.Empty);

        var bus = IpcMessageBusFactory.CreateServer(IpcTransport.Auto, endpoint);
        _buses.Add(bus);

        Assert.IsType<UnixDomainSocketMessageBus>(bus);
    }

    [Fact]
    public void Auto_FallsBackToNamedPipe_WhenUdsCannotBind()
    {
        // Linux UDS paths are limited (~108 bytes); an overlong path fails to
        // bind, and Auto must fall through to NamedPipe instead of throwing.
        var endpoint = new IpcEndpoint(
            UnixSocketPath: "/tmp/chorus-" + new string('x', 200) + ".sock",
            NamedPipeName: "chorus-test-fallback",
            TcpHost: "127.0.0.1",
            TcpPort: 0,
            HttpUrl: string.Empty);

        var bus = IpcMessageBusFactory.CreateServer(IpcTransport.Auto, endpoint);
        _buses.Add(bus);

        Assert.IsType<NamedPipeMessageBus>(bus);
    }

    private UnixDomainSocketMessageBus CreateServer()
    {
        var bus = new UnixDomainSocketMessageBus(_socketPath);
        _buses.Add(bus);
        bus.StartServer();
        return bus;
    }

    private static List<Envelope> CollectAsync(IMessageBus bus, CancellationToken ct)
    {
        var list = new List<Envelope>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in bus.SubscribeAsync(ct))
                {
                    list.Add(e);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        return list;
    }

    private static Envelope Env(string messageId) => new(
        Type: "test",
        MessageId: messageId,
        SessionId: "s-ipc",
        CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Body: System.Text.Json.JsonSerializer.SerializeToElement(new { v = messageId }));

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var bus in _buses)
        {
            try
            {
                await bus.DisposeAsync();
            }
            catch
            {
            }
        }

        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch
        {
        }
    }
}
