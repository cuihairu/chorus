using Chorus.Messaging.Transports;

namespace Chorus.Messaging;

/// <summary>
/// Publish-side fan-out over per-gateway eng-to-gw channels (stage-2
/// transport half): the worker publishes every engine message to every
/// configured gateway. A failing child never blocks the others' delivery;
/// failures surface to the caller as AggregateException. Subscribe is
/// publish-only by design — the worker receives commands on its own
/// gw-to-eng server bus, not through this wrapper.
/// </summary>
public sealed class FanOutMessageBus : IMessageBus
{
    private readonly IMessageBus[] _children;

    public FanOutMessageBus(IEnumerable<IMessageBus> children)
    {
        _children = children.ToArray();
        if (_children.Length == 0)
        {
            throw new ArgumentException("At least one child bus is required.", nameof(children));
        }
    }

    public async ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        foreach (var child in _children)
        {
            try
            {
                await child.PublishAsync(envelope, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(failures);
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var child in _children)
        {
            child.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<Envelope> SubscribeAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("FanOutMessageBus is publish-only; receive on the gw-to-eng server bus.");
}
