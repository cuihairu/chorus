using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;

namespace Chorus.Messaging.Transports;

public sealed class NamedPipeMessageBus : IMessageBus
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _acceptTasks = new();
    private readonly Channel<Envelope> _inbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    private Task? _serverTask;
    private NamedPipeClientStream? _client;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    public NamedPipeMessageBus(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        }
        _pipeName = pipeName;
    }

    public void StartServer(int maxInstances = 5)
    {
        if (_serverTask != null)
        {
            return;
        }

        _serverTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await server.WaitForConnectionAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    server.Dispose();
                    break;
                }

                var t = Task.Run(async () =>
                {
                    await using var s = server;
                    while (!_cts.IsCancellationRequested && s.IsConnected)
                    {
                        var frame = await LengthPrefixedStream.ReadFrameAsync(s, _cts.Token);
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
            _client ??= new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            if (!_client.IsConnected)
            {
                await _client.ConnectAsync(2000, cancellationToken);
            }

            var bytes = JsonEnvelopeCodec.Serialize(envelope);
            await LengthPrefixedStream.WriteFrameAsync(_client, bytes, cancellationToken);
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

        try { if (_serverTask != null) await _serverTask; } catch { }
        foreach (var t in _acceptTasks)
        {
            try { await t; } catch { }
        }

        await _clientLock.WaitAsync();
        try
        {
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
}
