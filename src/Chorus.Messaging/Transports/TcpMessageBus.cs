using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Chorus.Messaging.Transports;

public sealed class TcpMessageBus : IMessageBus
{
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _acceptTasks = new();
    private readonly Channel<Envelope> _inbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    private TcpListener? _listener;

    private TcpClient? _client;
    private NetworkStream? _clientStream;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    public TcpMessageBus(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host is required.", nameof(host));
        }
        if (port < 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        _host = host;
        _port = port;
    }

    public int ListeningPort { get; private set; }

    public void StartServer(int backlog = 128)
    {
        if (_listener != null)
        {
            return;
        }

        var address = ResolveListenAddress(_host);
        _listener = new TcpListener(address, _port);
        _listener.Start(backlog);

        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? accepted = null;
                try
                {
                    accepted = await _listener.AcceptTcpClientAsync(_cts.Token);
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
                    using var tcp = accepted;
                    tcp.NoDelay = true;
                    await using var stream = tcp.GetStream();
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
        }, _cts.Token);
    }

    public async ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await _clientLock.WaitAsync(cancellationToken);
        try
        {
            if (_client == null || _clientStream == null || !_client.Connected)
            {
                _clientStream?.Dispose();
                _client?.Dispose();

                _client = new TcpClient();
                _client.NoDelay = true;
                await _client.ConnectAsync(_host, _port, cancellationToken);
                _clientStream = _client.GetStream();
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
        _cts.Cancel();
        _inbox.Writer.TryComplete();

        try { _listener?.Stop(); } catch { }
        _listener = null;

        foreach (var t in _acceptTasks)
        {
            try { await t; } catch { }
        }

        await _clientLock.WaitAsync();
        try
        {
            _clientStream?.Dispose();
            _clientStream = null;
            _client?.Dispose();
            _client = null;
        }
        finally
        {
            _clientLock.Release();
            _clientLock.Dispose();
        }

        _cts.Dispose();
    }

    private static IPAddress ResolveListenAddress(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }
        if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) || host == "*")
        {
            return IPAddress.Any;
        }
        if (string.Equals(host, "::", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.IPv6Any;
        }
        if (IPAddress.TryParse(host, out var ip))
        {
            return ip;
        }
        return IPAddress.Loopback;
    }
}

