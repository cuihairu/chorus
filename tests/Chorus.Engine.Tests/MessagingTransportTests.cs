using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Chorus.Protocol;

namespace Chorus.Engine.Tests;

/// <summary>
/// Transport-level coverage: the NamedPipe bus's real publish/receive round
/// trip (server accept loop, length-prefixed frames, inbox), the IPC factory's
/// per-transport selection and configuration validation, and envelope codec
/// failure paths.
/// </summary>
public sealed class MessagingTransportTests
{
    private static Envelope EnvelopeOf(string messageId)
        => new(
            Type: "test",
            MessageId: messageId,
            SessionId: "s1",
            CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body: JsonSerializer.SerializeToElement(new { x = 1 }, ProtocolJson.SerializerOptions));

    // Probed ports, not fixed ones: a fixed port inside the Linux ephemeral
    // range (32768-60999) can be taken as a source port by any concurrent
    // localhost connection, and the listener bind then fails with "address
    // already in use" on a busy runner.
    private static IpcEndpoint Endpoint(string pipe = "chorus-test-factory")
        => new(
            UnixSocketPath: "/tmp/chorus-test.sock",
            NamedPipeName: pipe,
            TcpHost: "127.0.0.1",
            TcpPort: GetFreeTcpPort(),
            HttpUrl: $"http://127.0.0.1:{GetFreeTcpPort()}/");

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

    private static async Task<Envelope> ReceiveOneAsync(IMessageBus bus, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var item in bus.SubscribeAsync(cts.Token))
        {
            return item;
        }
        throw new InvalidOperationException("No message received.");
    }

    [Fact]
    public async Task NamedPipeBus_PublishesAndSubscribes()
    {
        var bus = new NamedPipeMessageBus($"chorus-test-roundtrip-{Guid.NewGuid():N}");
        bus.StartServer();

        try
        {
            var readTask = ReceiveOneAsync(bus, TimeSpan.FromSeconds(5));
            // Give the accept loop a beat to create the server pipe instance.
            await Task.Delay(100);
            await bus.PublishAsync(EnvelopeOf("m1"), CancellationToken.None);

            var got = await readTask;
            Assert.Equal("m1", got.MessageId);
            Assert.Equal(1, got.Body.GetProperty("x").GetInt32());
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    public async Task NamedPipeBus_ReusedClientConnection_SendsRepeatedly()
    {
        var bus = new NamedPipeMessageBus($"chorus-test-repeat-{Guid.NewGuid():N}");
        bus.StartServer();

        try
        {
            await Task.Delay(100);
            await bus.PublishAsync(EnvelopeOf("m1"), CancellationToken.None);
            await bus.PublishAsync(EnvelopeOf("m2"), CancellationToken.None);

            var ids = new List<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var item in bus.SubscribeAsync(cts.Token))
            {
                ids.Add(item.MessageId);
                if (ids.Count >= 2)
                {
                    break;
                }
            }

            Assert.Equal(["m1", "m2"], ids);
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    public void NamedPipeBus_EmptyPipeNameThrows()
    {
        Assert.Throws<ArgumentException>(() => new NamedPipeMessageBus(" "));
    }

    [Fact]
    public void Factory_CreateClient_SelectsTransportByKind()
    {
        var endpoint = Endpoint();

        Assert.IsType<UnixDomainSocketMessageBus>(IpcMessageBusFactory.CreateClient(IpcTransport.UnixDomainSocket, endpoint));
        Assert.IsType<NamedPipeMessageBus>(IpcMessageBusFactory.CreateClient(IpcTransport.NamedPipe, endpoint));
        Assert.IsType<TcpMessageBus>(IpcMessageBusFactory.CreateClient(IpcTransport.Tcp, endpoint));
        Assert.IsType<HttpMessageBus>(IpcMessageBusFactory.CreateClient(IpcTransport.Http, endpoint));
        // Auto picks UDS where the OS supports it, NamedPipe otherwise.
        var auto = IpcMessageBusFactory.CreateClient(IpcTransport.Auto, endpoint);
        Assert.Equal(Socket.OSSupportsUnixDomainSockets, auto is UnixDomainSocketMessageBus);
    }

    [Fact]
    public async Task Factory_CreateServer_StartsTcpHttpAndNamedPipe()
    {
        var endpoint = Endpoint(pipe: "chorus-test-factory-server");

        var tcp = IpcMessageBusFactory.CreateServer(IpcTransport.Tcp, endpoint);
        Assert.IsType<TcpMessageBus>(tcp);
        await tcp.DisposeAsync();

        var http = IpcMessageBusFactory.CreateServer(IpcTransport.Http, endpoint);
        Assert.IsType<HttpMessageBus>(http);
        await http.DisposeAsync();

        var pipe = IpcMessageBusFactory.CreateServer(IpcTransport.NamedPipe, endpoint);
        Assert.IsType<NamedPipeMessageBus>(pipe);
        await pipe.DisposeAsync();
    }

    [Fact]
    public void Factory_TcpWithoutHost_Throws()
    {
        var endpoint = new IpcEndpoint("/tmp/x.sock", "p", TcpHost: "", TcpPort: 45003, HttpUrl: "");

        Assert.Throws<InvalidOperationException>(
            () => IpcMessageBusFactory.CreateClient(IpcTransport.Tcp, endpoint));
        Assert.Throws<InvalidOperationException>(
            () => IpcMessageBusFactory.CreateServer(IpcTransport.Tcp, endpoint));
    }

    [Fact]
    public void Factory_TcpWithOutOfRangePort_Throws()
    {
        var endpoint = new IpcEndpoint("/tmp/x.sock", "p", TcpHost: "127.0.0.1", TcpPort: 0, HttpUrl: "");

        Assert.Throws<InvalidOperationException>(
            () => IpcMessageBusFactory.CreateClient(IpcTransport.Tcp, endpoint));
    }

    [Fact]
    public void Factory_HttpConfigurationValidation()
    {
        IpcEndpoint WithUrl(string url) => new("/tmp/x.sock", "p", HttpUrl: url);

        Assert.Throws<InvalidOperationException>(
            () => IpcMessageBusFactory.CreateClient(IpcTransport.Http, WithUrl("")));
        Assert.Throws<InvalidOperationException>(
            () => IpcMessageBusFactory.CreateClient(IpcTransport.Http, WithUrl("not-a-url")));
        Assert.Throws<InvalidOperationException>(
            () => IpcMessageBusFactory.CreateClient(IpcTransport.Http, WithUrl("ftp://host/x")));
        Assert.Throws<InvalidOperationException>(
            () => IpcMessageBusFactory.CreateServer(IpcTransport.Http, WithUrl("")));
    }

    [Fact]
    public void Factory_UnknownTransport_Throws()
    {
        var endpoint = new IpcEndpoint("/tmp/x.sock", "p");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => IpcMessageBusFactory.CreateClient((IpcTransport)99, endpoint));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IpcMessageBusFactory.CreateServer((IpcTransport)99, endpoint));
    }

    [Fact]
    public void Codec_RoundTripsEnvelope()
    {
        var env = EnvelopeOf("m-codec");
        var bytes = JsonEnvelopeCodec.Serialize(env);
        var back = JsonEnvelopeCodec.Deserialize(bytes);

        Assert.Equal("m-codec", back.MessageId);
        Assert.Equal(1, back.Body.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task Codec_NullEnvelope_FailsVisibly()
    {
        var bytes = "null"u8.ToArray();

        Assert.Throws<InvalidOperationException>(() => JsonEnvelopeCodec.Deserialize(bytes));

        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => JsonEnvelopeCodec.DeserializeAsync(stream, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Codec_MalformedJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => JsonEnvelopeCodec.Deserialize("{nope"u8.ToArray()));

        using var stream = new MemoryStream("{nope"u8.ToArray());
        await Assert.ThrowsAnyAsync<JsonException>(
            () => JsonEnvelopeCodec.DeserializeAsync(stream, CancellationToken.None).AsTask());
    }
}
