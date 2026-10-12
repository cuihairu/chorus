using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Chorus.Messaging;
using Chorus.Messaging.Transports;
using Chorus.Protocol;

namespace Chorus.Engine.Tests;

public sealed class HttpMessageBusTests
{
    [Fact]
    public async Task HttpBus_PublishesAndSubscribes()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}/ipc/";

        await using var server = new HttpMessageBus(url);
        server.StartServer();

        await using var client = new HttpMessageBus(url);

        var body = JsonSerializer.SerializeToElement(new { ok = true }, ProtocolJson.SerializerOptions);
        var env = new Envelope(
            Type: "test",
            MessageId: "m1",
            SessionId: "s1",
            CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body: body
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var readTask = Task.Run(async () =>
        {
            await foreach (var item in server.SubscribeAsync(cts.Token))
            {
                return item;
            }
            throw new InvalidOperationException("No message received.");
        }, cts.Token);

        await client.PublishAsync(env, cts.Token);
        var got = await readTask;
        Assert.Equal("m1", got.MessageId);
        Assert.True(got.Body.GetProperty("ok").GetBoolean());
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
