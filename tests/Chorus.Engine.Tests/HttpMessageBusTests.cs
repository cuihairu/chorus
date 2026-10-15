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

        // Safety net only - the read task returns on the first message; a
        // 5s budget raced HttpListener startup under full-suite load.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
    [Fact]
    public async Task HttpBus_DoubleDisposeIsNoOp()
    {
        var bus = new HttpMessageBus("http://127.0.0.1:1/unused");
        await bus.DisposeAsync();

        await bus.DisposeAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankUrl_Throws(string? url)
    {
        Assert.Throws<ArgumentException>(() => new HttpMessageBus(url!));
    }

    [Fact]
    public void Constructor_NormalizesTrailingSlash()
    {
        // 前缀语义要求尾斜杠：HttpListener 挂前缀、POST 落同 URL。
        Assert.EndsWith("/", new HttpMessageBus("http://127.0.0.1:1/x").Url);
        Assert.Equal("http://127.0.0.1:1/x/", new HttpMessageBus("http://127.0.0.1:1/x").Url);
        Assert.Equal("http://127.0.0.1:1/x/", new HttpMessageBus("http://127.0.0.1:1/x/").Url);
    }

    [Fact]
    public async Task HttpBus_StartServerTwice_IsIdempotent_AndStillServes()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}/ipc/";
        await using var server = new HttpMessageBus(url);
        server.StartServer();
        server.StartServer();

        await using var client = new HttpMessageBus(url);
        var env = MakeEnvelope("m2");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
    public async Task HttpBus_NonPostRequest_Returns405()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}/ipc/";
        await using var server = new HttpMessageBus(url);
        server.StartServer();

        using var http = new HttpClient();
        using var response = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task HttpBus_UndecodableBody_Returns400()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}/ipc/";
        await using var server = new HttpMessageBus(url);
        server.StartServer();

        using var http = new HttpClient();
        using var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await http.PostAsync(url, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HttpBus_AfterDispose_PublishFailsVisible()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}/ipc/";
        var server = new HttpMessageBus(url);
        server.StartServer();
        await using var client = new HttpMessageBus(url);

        await server.DisposeAsync();

        // 监听已停：发布必须 fail-visible（连接拒绝），而不是静默假成功。
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            async () => await client.PublishAsync(MakeEnvelope("m3"), CancellationToken.None));
    }

    private static Envelope MakeEnvelope(string messageId) => new(
        Type: "test",
        MessageId: messageId,
        SessionId: "s1",
        CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Body: JsonSerializer.SerializeToElement(new { ok = true }, ProtocolJson.SerializerOptions)
    );
}
