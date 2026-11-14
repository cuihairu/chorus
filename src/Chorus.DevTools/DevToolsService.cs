using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Threading.Channels;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;
using Microsoft.Extensions.Hosting;

namespace Chorus.DevTools;

/// <summary>The devtools channel's knobs. The transport/dir/prefix keys mirror
/// the IPC section's own (Ipc:Transport / Ipc:UnixSocketDir / Ipc:NamedPipePrefix),
/// so one configuration drives both channels; the defaults match IpcSettings.</summary>
public sealed class DevToolsOptions
{
    /// <summary>Auto picks UDS where the kernel has it and NamedPipe elsewhere,
    /// the same rule IpcMessageBusFactory applies. The devtools plane is a
    /// same-machine socket: Tcp/Http answer at the start, not mid-flight.</summary>
    public IpcTransport Transport { get; set; } = IpcTransport.Auto;
    public string UnixSocketDir { get; set; } = "/tmp/chorus";
    /// <summary>Names both endpoints, the way IpcEndpointBuilder uses
    /// NamedPipePrefix for the UDS files too: {prefix}_devtools.sock /
    /// {prefix}.devtools.</summary>
    public string PipePrefix { get; set; } = "chorus";
    public int MetricsIntervalMs { get; set; } = 1000;
    public int LogQueueCapacity { get; set; } = 4096;
    /// <summary>The metrics composition. The EngineHost passes its own
    /// ProcessCpuGauge so the push carries the /metrics face's caliber; the
    /// default snapshot is process-only (no CPU until a sampler supplies it).</summary>
    public Func<DevMetricsSnapshot> MetricsSampler { get; set; } = () => DevMetricsSnapshot.SelfProcess();
}

/// <summary>
/// The devtools push channel (IDE-EXT): one optional socket the host exposes
/// for IDE attach. Every attached client receives the same frames — dev.metrics
/// snapshots on a fixed cadence, dev.log entries forwarded from the host's
/// ILogger pipeline — riding the shared IPC framing (4-byte LE length prefix +
/// JSON envelope), so the IDE-side core decodes the same bytes the gateway
/// channels ride.
///
/// The bus classes cannot carry this shape: their publish path dials one client
/// connection of the bus's own (receiver binds, sender connects — EngineIpcService's
/// _outgoing is a CreateClient), so a server instance cannot push to the clients
/// attached to it. This service owns its accept loop instead, reusing every
/// framing primitive above it (RawUnixEndPoint, FrameReader,
/// LengthPrefixedStream, JsonEnvelopeCodec) unchanged.
///
/// The channel is a development surface: the host registers it only behind its
/// own explicit flag and refuses it in Production (the Ingress:AllowAll
/// discipline), so a default host never binds the socket.
/// </summary>
public sealed class DevToolsService(DevToolsOptions options, Channel<DevLogEntry> logQueue) : BackgroundService
{
    /// <summary>The well-known non-empty session id for devtools envelopes —
    /// EnvelopeFactory rejects empty ones; the IPC heartbeat's "__heartbeat"
    /// sentinel is the same device.</summary>
    public const string SessionId = "__devtools";

    // The pipe bus's 2-second connect budget, applied to writes: an IDE that
    // stopped reading falls off instead of stalling the other clients.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    private Socket? _listenSocket;
    private string? _socketPath;
    private DateTime _boundAt;
    private bool _hasIdentity;

    /// <summary>Starts the channel: the UDS bind happens here, before
    /// ExecuteAsync is scheduled — .NET 10 starts the execute task on the
    /// thread pool, so a bind inside it leaves a window where the well-known
    /// path does not answer yet and an IDE attaching right after host start
    /// would race it. Binding here gives the start the same determinism the
    /// UDS bus's StartServer gives its callers. (The named-pipe transport has
    /// no bind step: its first server instance is the accept, and clients
    /// wait for the pipe by design.)</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (UseUds(options.Transport))
        {
            // The UDS bus's own bind discipline: a stale path falls, the
            // directory is made, and the bind goes through RawUnixEndPoint so
            // this process keeps the deletion right (a BCL-bound path is
            // unlinked by Socket.Dispose, dotnet/runtime#52103).
            _socketPath = UnixSocketPath();
            if (File.Exists(_socketPath))
            {
                try { File.Delete(_socketPath); } catch { }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
            _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listenSocket.Bind(new RawUnixEndPoint(_socketPath));
            _listenSocket.Listen(8);
            // Identity guard (st_ctime, the bus's own): only ever delete the
            // file this bind created — a rolling restart's new bind must
            // survive this process's teardown.
            try { _boundAt = File.GetCreationTimeUtc(_socketPath); _hasIdentity = true; } catch { }
        }
        await base.StartAsync(cancellationToken);
    }

    private static bool UseUds(IpcTransport transport) => transport switch
    {
        IpcTransport.Auto => Socket.OSSupportsUnixDomainSockets,
        IpcTransport.UnixDomainSocket => true,
        IpcTransport.NamedPipe => false,
        // A network transport answers at the start: the devtools plane is a
        // same-machine socket (UDS/NamedPipe), and a channel that cannot bind
        // never comes up — the same fail-fast the IPC endpoint validation
        // applies to inconsistent shard settings.
        _ => throw new ArgumentException(
            $"DevTools carries a same-machine socket transport; {transport} is not one"),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_listenSocket is not null)
            {
                try
                {
                    await Task.WhenAll(
                        AcceptSocketsAsync(_listenSocket, stoppingToken),
                        PushLoopAsync(stoppingToken));
                }
                finally
                {
                    if (_hasIdentity)
                    {
                        try { if (File.GetCreationTimeUtc(_socketPath) == _boundAt) File.Delete(_socketPath); }
                        catch { }
                    }
                    _listenSocket.Dispose();
                }
            }
            else
            {
                await RunPipeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The devtools channel must never take the engine with it: a broken
            // channel is one stderr line and a socket that stops answering.
            Console.Error.WriteLine($"[devtools] channel down ({ex.Message}); IDE attach stays closed.");
        }
        finally
        {
            foreach (var connection in _connections.Values.ToArray())
                Remove(connection);
        }
    }

    private async Task AcceptSocketsAsync(Socket listen, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Socket accepted;
            try { accepted = await listen.AcceptAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[devtools] accept failed ({ex.Message})");
                break;
            }
            Register(new Connection(new NetworkStream(accepted, ownsSocket: true)));
        }
    }

    private async Task RunPipeAsync(CancellationToken stoppingToken)
    {
        // The same two loops the UDS path runs, side by side: accepting one
        // connection per IDE and pushing to every one of them.
        await Task.WhenAll(
            AcceptPipesAsync(stoppingToken),
            PushLoopAsync(stoppingToken));
    }

    private async Task AcceptPipesAsync(CancellationToken stoppingToken)
    {
        var pipeName = PipeName();
        while (!stoppingToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            catch
            {
                // A rejected pipe (the client vanished between connect and
                // accept) is one more turn of the loop, not a dead channel.
                server.Dispose();
                continue;
            }
            Register(new Connection(server));
        }
    }

    private void Register(Connection connection)
    {
        _connections[connection.Id] = connection;
        _ = Task.Run(() => ReadLoopAsync(connection));
    }

    /// <summary>The read loop is disconnect detection: the devtools plane is
    /// push-only today, so inbound frames are tolerated and dropped (future IDE
    /// commands stay additive); the read ending is the client leaving.</summary>
    private async Task ReadLoopAsync(Connection connection)
    {
        try
        {
            using var reader = new FrameReader(connection.Stream);
            while (await reader.ReadFrameAsync(CancellationToken.None) is not null)
            {
            }
        }
        catch
        {
            // A torn frame or a dropped socket reads the same here: gone.
        }
        finally
        {
            Remove(connection);
        }
    }

    private async Task PushLoopAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            MetricsLoopAsync(stoppingToken),
            LogDrainLoopAsync(stoppingToken));
    }

    private async Task MetricsLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, options.MetricsIntervalMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            await BroadcastAsync(
                EnvelopeFactory.Create(IpcMessageTypes.DevMetrics, SessionId, options.MetricsSampler()),
                stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task LogDrainLoopAsync(CancellationToken stoppingToken)
    {
        // The queue is DropOldest bounded: logging never blocks and never
        // throws on a slow channel — the oldest history falls off instead.
        await foreach (var entry in logQueue.Reader.ReadAllAsync(stoppingToken))
        {
            await BroadcastAsync(
                EnvelopeFactory.Create(IpcMessageTypes.DevLog, SessionId, entry),
                stoppingToken);
        }
    }

    private async Task BroadcastAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // Serialize once; every attached IDE gets the same bytes (one messageId
        // seen by all — the tests pin it).
        var bytes = JsonEnvelopeCodec.Serialize(envelope);
        foreach (var connection in _connections.Values.ToArray())
        {
            try { await connection.WriteAsync(bytes, cancellationToken); }
            catch { Remove(connection); }
        }
    }

    private void Remove(Connection connection)
    {
        _connections.TryRemove(connection.Id, out _);
        connection.Dispose();
    }

    private string UnixSocketPath()
    {
        var dir = string.IsNullOrWhiteSpace(options.UnixSocketDir) ? "/tmp/chorus" : options.UnixSocketDir;
        var prefix = string.IsNullOrWhiteSpace(options.PipePrefix) ? "chorus" : options.PipePrefix;
        return Path.Combine(dir, $"{prefix}_devtools.sock");
    }

    private string PipeName()
    {
        var prefix = string.IsNullOrWhiteSpace(options.PipePrefix) ? "chorus" : options.PipePrefix;
        return $"{prefix}.devtools";
    }

    /// <summary>One attached IDE: its stream, and the write lock that keeps this
    /// connection's frames in FIFO order across the concurrent push loops.</summary>
    private sealed class Connection : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Stream Stream { get; }

        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Connection(Stream stream) { Stream = stream; }

        public async Task WriteAsync(byte[] frame, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(WriteTimeout);
                await LengthPrefixedStream.WriteFrameAsync(Stream, frame, bounded.Token);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            // The write lock is deliberately NOT disposed: Remove() races the
            // other push loop (metrics and logs drain concurrently), and a
            // writer holding or awaiting the semaphore when it dies turns
            // Release()/WaitAsync() into ObjectDisposedException — poisoning
            // that loop and taking every client down with it. A caught write
            // on the closed stream is contained instead (each BroadcastAsync
            // try/catches, Remove is idempotent, Dispose of the semaphore
            // buys nothing: this lock never touches AvailableWaitHandle, so
            // it owns no OS handle for the GC to collect).
            Stream.Dispose();
        }
    }
}
