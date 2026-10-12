using System.Text.Json;
using Chorus.Messaging;
using Chorus.Messaging.Transports;
using Chorus.Protocol;

namespace Chorus.Engine.Tests;

public sealed class TcpMessageBusTests
{
    [Fact]
    public async Task TcpBus_PublishesAndSubscribes()
    {
        await using var server = new TcpMessageBus("127.0.0.1", port: 0);
        server.StartServer();

        await using var client = new TcpMessageBus("127.0.0.1", server.ListeningPort);

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
    [Fact]
    public async Task TcpBus_DoubleDisposeIsNoOp()
    {
        var bus = new TcpMessageBus("127.0.0.1", 0);
        await bus.DisposeAsync();

        await bus.DisposeAsync();
    }
}
