using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Chorus.Messaging;

/// <summary>
/// Subscribe-side merge of the sharded eng-to-gw channels (todo §10 blocker
/// ②): each shard socket gets its own pump task, all of them funnel into one
/// bounded channel, and a single consumer yields the merged stream. The
/// gateway keeps today's single-consumer dispatch semantics while the engine
/// side writes to K sockets instead of one. Publish is receive-only by
/// design — publishes travel the gw-to-eng command channel.
/// </summary>
public sealed class FanInMessageBus : IMessageBus
{
    // The gateway dispatch loop is single-threaded and fast (route + forward);
    // 4096 in-flight envelopes bounds memory without throttling bursty ticks.
    private const int MergeCapacity = 4096;

    private readonly IMessageBus[] _children;

    public FanInMessageBus(IEnumerable<IMessageBus> children)
    {
        _children = children.ToArray();
        if (_children.Length == 0)
        {
            throw new ArgumentException("At least one child bus is required.", nameof(children));
        }
    }

    public IAsyncEnumerable<Envelope> SubscribeAsync(CancellationToken cancellationToken = default)
        => MergeAsync(cancellationToken);

    public ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
        => throw new NotSupportedException("FanInMessageBus is receive-only; publish on the gw-to-eng command bus.");

    private async IAsyncEnumerable<Envelope> MergeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var merge = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(MergeCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumping = PumpAllAsync(merge.Writer, pumps.Token);
        try
        {
            while (await merge.Reader.WaitToReadAsync(cancellationToken))
            {
                while (merge.Reader.TryRead(out var envelope))
                {
                    yield return envelope;
                }
            }
        }
        finally
        {
            pumps.Cancel();
            try
            {
                await pumping;
            }
            catch (OperationCanceledException) when (pumps.IsCancellationRequested)
            {
                // Consumer ended (completed children, cancellation, or early
                // break) — the pumps shutting down is the expected outcome.
            }
        }
    }

    /// <summary>Drains every child into the merge channel in child order and
    /// completes the channel once all children are done, so the consumer sees
    /// a clean end-of-stream exactly when the last shard socket closes.</summary>
    private async Task PumpAllAsync(ChannelWriter<Envelope> merge, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(_children.Select(child => PumpAsync(child, merge, cancellationToken)));
            merge.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            merge.TryComplete();
        }
        catch (Exception ex)
        {
            merge.TryComplete(ex);
        }
    }

    private static async Task PumpAsync(
        IMessageBus child, ChannelWriter<Envelope> merge, CancellationToken cancellationToken)
    {
        await foreach (var envelope in child.SubscribeAsync(cancellationToken))
        {
            await merge.WriteAsync(envelope, cancellationToken);
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
}
