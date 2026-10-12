using System.Text.Json;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Chorus.Protocol;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-3 acceptance (事件关联 ID): the platform EventId rides the IPC
/// envelope as <see cref="Envelope.SourceEventId"/> from ingress to engine, so
/// any delivery can be traced back to the original platform event.
/// </summary>
public sealed class CorrelationTests
{
    [Fact]
    public async Task SourceEventId_RoundTripsThroughBus()
    {
        var socketPath = $"/tmp/chorus-test-{Guid.NewGuid():N}.sock";
        await using var server = new UnixDomainSocketMessageBus(socketPath);
        await using var client = new UnixDomainSocketMessageBus(socketPath);
        server.StartServer();

        var body = JsonSerializer.SerializeToElement(new { v = 42 });
        var env = new Envelope(
            Type: "test.correlation",
            MessageId: "m_corr",
            SessionId: "s1",
            CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body: body,
            SourceEventId: "dy-evt-0001");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = Task.Run(async () =>
        {
            await foreach (var item in server.SubscribeAsync(cts.Token))
            {
                return item;
            }
            throw new InvalidOperationException("No message received.");
        }, cts.Token);

        await client.PublishAsync(env, cts.Token);
        var got = await received;

        Assert.Equal("dy-evt-0001", got.SourceEventId);
        Assert.Equal("m_corr", got.MessageId);

        cts.Cancel();
    }

    [Fact]
    public void Create_LeavesSourceEventIdNullByDefault()
    {
        var env = EnvelopeFactory.Create("test.type", "s1", new { v = 1 });

        Assert.Null(env.SourceEventId);
        Assert.StartsWith("m_", env.MessageId);
    }

    [Fact]
    public void Create_CarriesSourceEventId()
    {
        var env = EnvelopeFactory.Create("test.type", "s1", new { v = 1 }, sourceEventId: "ks-evt-9");

        Assert.Equal("ks-evt-9", env.SourceEventId);
    }
}
