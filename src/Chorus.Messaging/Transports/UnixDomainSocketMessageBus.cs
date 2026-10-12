using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Chorus.Messaging.Transports;

public sealed class UnixDomainSocketMessageBus : IMessageBus
{
    private readonly string _socketPath;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly ConcurrentBag<Task> _acceptTasks = new();
    private readonly Channel<Envelope> _inbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    private Task? _serverTask;
    private Socket? _listenSocket;
    private Socket? _clientSocket;
    private NetworkStream? _clientStream;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    public UnixDomainSocketMessageBus(string socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            throw new ArgumentException("Socket path is required.", nameof(socketPath));
        }
        _socketPath = socketPath;
    }

    public void StartServer(int backlog = 32)
    {
        if (_serverTask != null)
        {
            return;
        }

        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); } catch { }
        }

        var dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listenSocket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listenSocket.Listen(backlog);

        _serverTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket? accepted = null;
                try
                {
                    accepted = await _listenSocket.AcceptAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                var t = Task.Run(async () =>
                {
                    using var socket = accepted;
                    await using var stream = new NetworkStream(socket, ownsSocket: false);
                    while (!_cts.IsCancellationRequested)
                    {
                        byte[]? frame;
                        try
                        {
                            frame = await LengthPrefixedStream.ReadFrameAsync(stream, _cts.Token);
                        }
                        catch
                        {
                            break;
                        }

                        if (frame == null)
                        {
                            break;
                        }

                        var env = JsonEnvelopeCodec.Deserialize(frame);
                        await _inbox.Writer.WriteAsync(env, _cts.Token);
                    }
                }, _cts.Token);

                _acceptTasks.Add(t);
            }

            if (File.Exists(_socketPath))
            {
                try { File.Delete(_socketPath); } catch { }
            }
        }, _cts.Token);
    }

    public async ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await _clientLock.WaitAsync(cancellationToken);
        try
        {
            if (_clientSocket == null || _clientStream == null || !_clientSocket.Connected)
            {
                _clientStream?.Dispose();
                _clientSocket?.Dispose();
                _clientSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await _clientSocket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken);
                _clientStream = new NetworkStream(_clientSocket, ownsSocket: false);
            }

            var bytes = JsonEnvelopeCodec.Serialize(envelope);
            await LengthPrefixedStream.WriteFrameAsync(_clientStream, bytes, cancellationToken);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    public async IAsyncEnumerable<Envelope> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _inbox.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_inbox.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        _inbox.Writer.TryComplete();

        try { if (_serverTask != null) await _serverTask; } catch { }
        foreach (var t in _acceptTasks)
        {
            try { await t; } catch { }
        }

        try
        {
            _listenSocket?.Dispose();
            _listenSocket = null;
        }
        catch
        {
        }

        await _clientLock.WaitAsync();
        try
        {
            _clientStream?.Dispose();
            _clientStream = null;
            _clientSocket?.Dispose();
            _clientSocket = null;
        }
        finally
        {
            _clientLock.Release();
            _clientLock.Dispose();
        }

        _cts.Dispose();
    }
}
