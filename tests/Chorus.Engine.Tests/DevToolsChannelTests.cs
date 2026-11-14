using System.IO.Pipes;
using System.Net.Sockets;
using System.Threading.Channels;
using Chorus.DevTools;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The devtools channel's moving parts (IDE-EXT, engine side): one optional push
/// socket the host turns on with --devtools. Every fact rides the real framing —
/// the 4-byte LE length prefix and the JSON envelope over the same FrameReader
/// and JsonEnvelopeCodec the gateway channels use — so the IDE-side core (the
/// devcore package) attaches to verified bytes, not a parallel wire.
/// </summary>
public sealed class DevToolsChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chorus-devtools-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task MetricsFramesFanOutToEveryAttachedClient()
    {
        var options = new DevToolsOptions
        {
            UnixSocketDir = _dir,
            MetricsIntervalMs = 50,
            MetricsSampler = () => DevMetricsSnapshot.SelfProcess(42.5),
        };
        var queue = Channel.CreateBounded<DevLogEntry>(new BoundedChannelOptions(64));
        using var service = new DevToolsService(options, queue);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        await service.StartAsync(stopping.Token);

        var path = Path.Combine(_dir, "chorus_devtools.sock");
        using var clientA = await DevClient.ConnectAsync(path);
        using var clientB = await DevClient.ConnectAsync(path);

        // The two sockets may attach on either side of a tick, so the older
        // frame advances until both clients read the same broadcast — one
        // serialization fanned to every attached IDE.
        var a = await clientA.ReadAsync(IpcMessageTypes.DevMetrics);
        var b = await clientB.ReadAsync(IpcMessageTypes.DevMetrics);
        for (var attempts = 0; attempts < 20 && a.MessageId != b.MessageId; attempts++)
        {
            if (a.CreatedTimeMs <= b.CreatedTimeMs) a = await clientA.ReadAsync(IpcMessageTypes.DevMetrics);
            else b = await clientB.ReadAsync(IpcMessageTypes.DevMetrics);
        }
        Assert.Equal(a.MessageId, b.MessageId);

        Assert.Equal(IpcMessageTypes.DevMetrics, a.Type);
        Assert.Equal(DevToolsService.SessionId, a.SessionId);
        var snapshot = EnvelopeFactory.DeserializeBody<DevMetricsSnapshot>(a);
        Assert.Equal(42.5, snapshot.CpuPercent);
        Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
        Assert.True(snapshot.TimestampMs > 0);

        // The channel's own file falls with the service: the identity-guarded
        // delete (the UDS bus's st_ctime discipline) leaves no stale path.
        stopping.Cancel();
        await service.StopAsync(CancellationToken.None);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task LogEntriesFlowFromTheLoggerPipelineToTheAttachedClient()
    {
        var options = new DevToolsOptions { UnixSocketDir = _dir, MetricsIntervalMs = 50 };
        var queue = Channel.CreateBounded<DevLogEntry>(new BoundedChannelOptions(64));
        using var service = new DevToolsService(options, queue);
        await service.StartAsync(_stop.Token);

        using var client = await DevClient.ConnectAsync(Path.Combine(_dir, "chorus_devtools.sock"));
        var provider = new DevToolsLoggerProvider(queue);
        var logger = provider.CreateLogger("Chorus.Engine.Tests.DevTools");
        logger.LogInformation("hello {Subject}", "world");
        logger.LogWarning(new InvalidOperationException("boom"), "wobble {Count}", 3);

        var envelope = await client.ReadAsync(IpcMessageTypes.DevLog);
        var entry = EnvelopeFactory.DeserializeBody<DevLogEntry>(envelope);
        Assert.Equal(IpcMessageTypes.DevLog, envelope.Type);
        Assert.Equal("Information", entry.Level);
        Assert.Equal("Chorus.Engine.Tests.DevTools", entry.Category);
        Assert.Equal("hello world", entry.Body);
        Assert.True(entry.TimestampMs > 0);

        var second = EnvelopeFactory.DeserializeBody<DevLogEntry>(
            await client.ReadAsync(IpcMessageTypes.DevLog));
        Assert.Equal("Warning", second.Level);
        Assert.StartsWith("wobble 3", second.Body);
        Assert.Contains("boom", second.Body); // the exception rides beneath the message
    }

    [Fact]
    public void FullQueueDropsTheOldestLogEntriesInsteadOfBlocking()
    {
        var queue = Channel.CreateBounded<DevLogEntry>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var provider = new DevToolsLoggerProvider(queue);
        var logger = provider.CreateLogger("category");
        logger.LogInformation("one");
        logger.LogInformation("two");
        logger.LogInformation("three");

        var bodies = new List<string>();
        while (queue.Reader.TryRead(out var entry)) bodies.Add(entry.Body);
        Assert.Equal(["two", "three"], bodies);
    }

    [Fact]
    public async Task ADeadClientFallsOffWithoutDisturbingTheOthers()
    {
        var options = new DevToolsOptions
        {
            UnixSocketDir = _dir,
            MetricsIntervalMs = 50,
            MetricsSampler = () => DevMetricsSnapshot.SelfProcess(7.5),
        };
        var queue = Channel.CreateBounded<DevLogEntry>(new BoundedChannelOptions(64));
        using var service = new DevToolsService(options, queue);
        await service.StartAsync(_stop.Token);

        var path = Path.Combine(_dir, "chorus_devtools.sock");
        var doomed = await DevClient.ConnectAsync(path);
        using var survivor = await DevClient.ConnectAsync(path);
        // Warm both sockets so the doomed one is a broadcast target, then drop it.
        await doomed.ReadAsync(IpcMessageTypes.DevMetrics);
        await survivor.ReadAsync(IpcMessageTypes.DevMetrics);
        doomed.Dispose();

        // The survivor keeps reading across ticks while the dead socket falls
        // off; the service itself never faults.
        for (var i = 0; i < 3; i++)
        {
            await survivor.ReadAsync(IpcMessageTypes.DevMetrics);
        }
        Assert.NotNull(service.ExecuteTask);
        Assert.False(service.ExecuteTask!.IsFaulted);
    }

    [Fact]
    public async Task TheNamedPipeTransportServesTheSameFrames()
    {
        var prefix = "chorus-devtools-tests-" + Guid.NewGuid().ToString("N")[..8];
        var options = new DevToolsOptions
        {
            Transport = IpcTransport.NamedPipe,
            PipePrefix = prefix,
            MetricsIntervalMs = 50,
            MetricsSampler = () => DevMetricsSnapshot.SelfProcess(11.25),
        };
        var queue = Channel.CreateBounded<DevLogEntry>(new BoundedChannelOptions(64));
        using var service = new DevToolsService(options, queue);
        await service.StartAsync(_stop.Token);

        using var pipe = new NamedPipeClientStream(".", $"{prefix}.devtools", PipeDirection.InOut);
        await pipe.ConnectAsync(5000);
        using var client = new DevClient(pipe);

        var envelope = await client.ReadAsync(IpcMessageTypes.DevMetrics);
        var snapshot = EnvelopeFactory.DeserializeBody<DevMetricsSnapshot>(envelope);
        Assert.Equal(IpcMessageTypes.DevMetrics, envelope.Type);
        Assert.Equal(11.25, snapshot.CpuPercent);
    }

    [Fact]
    public async Task NonSocketTransportsAnswerAtTheStart()
    {
        var service = new DevToolsService(
            new DevToolsOptions { Transport = IpcTransport.Tcp },
            Channel.CreateBounded<DevLogEntry>(new BoundedChannelOptions(2)));
        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            () => service.StartAsync(CancellationToken.None));
        Assert.Contains("Tcp", thrown.Message);
        service.Dispose();
    }

    /// <summary>One attached IDE socket: a FrameReader over the same framing the
    /// gateway channels ride, reading until a wanted frame type lands.</summary>
    private sealed class DevClient(Stream stream) : IDisposable
    {
        private readonly FrameReader _reader = new(stream);

        public static async Task<DevClient> ConnectAsync(string path)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            // The client side dials with the BCL endpoint — the same choice the
            // UDS bus's own client path makes; RawUnixEndPoint exists to own the
            // bind side's deletion right, which a connecting client never holds.
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
            return new DevClient(new NetworkStream(socket, ownsSocket: true));
        }

        /// <summary>Metrics and logs share the socket, so the reader skips the
        /// frame types it did not ask for.</summary>
        public async Task<Envelope> ReadAsync(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var frame = await _reader.ReadFrameAsync(timeout.Token)
                    ?? throw new IOException($"the devtools socket closed before a {type} frame arrived");
                var envelope = JsonEnvelopeCodec.Deserialize(frame);
                if (envelope.Type == type) return envelope;
            }
        }

        public void Dispose()
        {
            _reader.Dispose(); // returns the pooled buffer, does not close the stream
            stream.Dispose(); // actually hangs up: the server's read loop sees EOF
        }
    }
}
