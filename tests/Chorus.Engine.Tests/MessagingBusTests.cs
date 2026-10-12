using System.Text.Json;
using Chorus.Messaging;
using Chorus.Messaging.Transports;
using Chorus.Protocol;

namespace Chorus.Engine.Tests;

public sealed class MessagingBusTests
{
    [Fact]
    public async Task InProcBus_PublishesAndSubscribes()
    {
        await using var bus = new InProcMessageBus();

        var body = JsonSerializer.SerializeToElement(new { x = 1 }, ProtocolJson.SerializerOptions);
        var env = new Envelope(
            Type: "test",
            MessageId: "m1",
            SessionId: "s1",
            CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body: body
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var readTask = Task.Run(async () =>
        {
            await foreach (var item in bus.SubscribeAsync(cts.Token))
            {
                return item;
            }
            throw new InvalidOperationException("No message received.");
        }, cts.Token);

        await bus.PublishAsync(env, cts.Token);
        var got = await readTask;
        Assert.Equal(env.Type, got.Type);
        Assert.Equal(env.MessageId, got.MessageId);
        Assert.Equal(env.SessionId, got.SessionId);
        Assert.Equal(1, got.Body.GetProperty("x").GetInt32());
    }
}

