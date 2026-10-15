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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankSocketPath_Throws(string? path)
    {
        Assert.Throws<ArgumentException>(() => new UnixDomainSocketMessageBus(path!));
    }

    [Fact]
    public async Task UnixDomainSocketBus_StartServerTwice_IsIdempotent_AndStillServes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"chorus_{Guid.NewGuid():N}.sock");
        await using var server = new UnixDomainSocketMessageBus(socketPath);
        server.StartServer();
        server.StartServer();

        await using var client = new UnixDomainSocketMessageBus(socketPath);
        var env = MakeEnvelope("m2");
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
        Assert.Equal("m2", (await readTask).MessageId);
    }

    [Fact]
    public async Task UnixDomainSocketBus_StartServer_RemovesStaleFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // 残留同名普通文件（上次进程异常退出的 socket 文件等）不得让绑定失败：
        // StartServer 先删再绑。
        var socketPath = Path.Combine(Path.GetTempPath(), $"chorus_{Guid.NewGuid():N}.sock");
        File.WriteAllText(socketPath, "stale");
        await using var server = new UnixDomainSocketMessageBus(socketPath);
        server.StartServer();

        await using var client = new UnixDomainSocketMessageBus(socketPath);
        var env = MakeEnvelope("m3");
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
        Assert.Equal("m3", (await readTask).MessageId);
    }

    private static Envelope MakeEnvelope(string messageId) => new(
        Type: "test",
        MessageId: messageId,
        SessionId: "s1",
        CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Body: System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true }, ProtocolJson.SerializerOptions)
    );
}
