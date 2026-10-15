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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_BlankHost_Throws(string? host)
    {
        Assert.Throws<ArgumentException>(() => new TcpMessageBus(host!, port: 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Constructor_PortOutOfRange_Throws(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TcpMessageBus("127.0.0.1", port));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("no.such.host.invalid")]
    public async Task StartServer_ResolvesHosts_AndListens(string host)
    {
        // 各主机写法都要落到可监听地址（回环 / IPv4 通配 / IPv6 通配 / 字面 IP；
        // 其余无法解析的主机名 fail-open 回环，port 0 交给内核分配。
        await using var bus = new TcpMessageBus(host, port: 0);

        bus.StartServer();

        Assert.True(bus.ListeningPort > 0);
    }

    [Fact]
    public async Task StartServer_Twice_IsIdempotent()
    {
        await using var bus = new TcpMessageBus("127.0.0.1", port: 0);

        bus.StartServer();
        bus.StartServer();

        Assert.True(bus.ListeningPort > 0);
    }
}
