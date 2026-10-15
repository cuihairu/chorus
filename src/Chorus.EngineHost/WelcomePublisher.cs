using System.Threading.Channels;
using Chorus.Messaging;

namespace Chorus.EngineHost;

/// <summary>
/// Welcome delivery off the engine's receive loop. The receive loop serves every
/// gw-&gt;eng envelope on one thread, and a <c>GatewayConnect</c> used to inline-
/// await its welcome publish over the eng-&gt;gw bus. That publish shares one
/// socket and one lock with every snapshot fan-out publish, so when the gateway
/// cannot drain fast enough (2026-10-15 gate run: 21.5k eng-&gt;gw messages/s at
/// 800 rooms x 2,400 clients) the holder blocks inside the kernel send buffer
/// and the receive loop stalls behind it - audience events stopped arriving
/// altogether while connects kept flowing. Welcomes are queued here instead: a
/// stalled bus can now only stall welcome delivery (visible as publish
/// failures), never the event path.
/// </summary>
public sealed class WelcomePublisher : IAsyncDisposable
{
    /// <summary>Default queued welcomes before a welcome is dropped. A few
    /// seconds of worst-case connect churn at the gate scale.</summary>
    public const int DefaultCapacity = 4096;

    private readonly Channel<Envelope> _queue;
    private readonly Func<Envelope, CancellationToken, Task> _publish;
    private readonly Action<Envelope, Exception>? _onFailure;
    private readonly int _capacity;
    private long _dropped;
    private Task? _task;
    private int _started;

    public WelcomePublisher(
        Func<Envelope, CancellationToken, Task> publish,
        int capacity = DefaultCapacity,
        Action<Envelope, Exception>? onFailure = null)
    {
        _publish = publish;
        _capacity = Math.Max(1, capacity);
        _onFailure = onFailure;
        _queue = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(_capacity)
        {
            // TryWrite is the only producer path, so the mode never blocks it.
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Welcomes dropped because the queue was full or their publish
    /// failed - both count as failed welcomes for the /metrics surface.</summary>
    public long Failed => Interlocked.Read(ref _dropped);

    /// <summary>Starts the publisher task. Idempotent.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _task = Task.Run(async () =>
        {
            try
            {
                await foreach (var env in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await _publish(env, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RecordFailure(env, ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Queue a welcome for delivery. Never awaits the bus, so the receive loop
    /// keeps consuming. Returns false (and counts a failure) when the queue is
    /// full - the welcome is dropped rather than blocking the event path.
    /// </summary>
    public bool TryEnqueue(Envelope envelope)
    {
        if (_queue.Writer.TryWrite(envelope))
        {
            return true;
        }

        RecordFailure(envelope, new InvalidOperationException("Welcome publish queue is full; welcome dropped."));
        return false;
    }

    private void RecordFailure(Envelope envelope, Exception ex)
    {
        Interlocked.Increment(ref _dropped);
        _onFailure?.Invoke(envelope, ex);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        if (_task != null)
        {
            try
            {
                await _task.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
