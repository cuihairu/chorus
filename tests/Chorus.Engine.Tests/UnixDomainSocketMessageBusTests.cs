using System.Runtime.InteropServices;
using System.Text.Json;
using Chorus.Messaging;
using Chorus.Messaging.Transports;
using Chorus.Protocol;

namespace Chorus.Engine.Tests;

public sealed class UnixDomainSocketMessageBusTests
{
    [Fact]
    public async Task UnixDomainSocketBus_PublishesAndSubscribes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"chorus_{Guid.NewGuid():N}.sock");

        await using var server = new UnixDomainSocketMessageBus(socketPath);
        server.StartServer();

        await using var client = new UnixDomainSocketMessageBus(socketPath);

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
    public async Task UnixDomainSocketBus_DoubleDisposeIsNoOp()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"chorus_{Guid.NewGuid():N}.sock");
        var bus = new UnixDomainSocketMessageBus(socketPath);
        bus.StartServer();
        await bus.DisposeAsync();

        await bus.DisposeAsync();
    }
}
