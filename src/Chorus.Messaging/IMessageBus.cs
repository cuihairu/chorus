namespace Chorus.Messaging;

public interface IMessageBus : IAsyncDisposable
{
    ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken);

    IAsyncEnumerable<Envelope> SubscribeAsync(CancellationToken cancellationToken);
}

