using System.Threading.Channels;

namespace Chorus.Messaging.Transports;

public sealed class InProcMessageBus : IMessageBus
{
    private readonly Channel<Envelope> _channel;

    public InProcMessageBus(int? capacity = null)
    {
        _channel = capacity is > 0
            ? Channel.CreateBounded<Envelope>(new BoundedChannelOptions(capacity.Value)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            })
            : Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
            });
    }

    public ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(envelope, cancellationToken);

    public async IAsyncEnumerable<Envelope> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_channel.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

