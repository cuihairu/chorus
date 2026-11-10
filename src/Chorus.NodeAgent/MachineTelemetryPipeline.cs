using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Chorus.NodeAgent;

/// <summary>
/// The machine telemetry reliability core (FIX-OBS-06): a bounded buffer between the
/// sampling loop and the reporting loop, with a bounded per-sample retry and a best-effort
/// flush on shutdown.
/// <list type="bullet">
/// <item><b>Buffer cap + over-limit policy</b>: a sample is never silently lost to a full
/// buffer — the oldest buffered sample is dropped so the newest always lands (a stale gauge
/// is worth less than a fresh one). The drop is counted and visible.</item>
/// <item><b>Bounded retry</b>: each sample is attempted up to <c>maxAttempts</c> with a
/// <c>retryBackoff</c> between attempts; when every attempt fails the sample is dropped and
/// counted (given up) rather than retried forever — a node whose control plane is gone must
/// not accumulate an unbounded backlog.</item>
/// <item><b>Flush before exit</b>: <see cref="FlushAsync"/> drains the buffer with one
/// attempt each and no backoff — shutdown has no time to back off, and the newest buffered
/// sample still gets its chance to land.</item>
/// </list>
/// Retryable failures are the transport's own (<see cref="RpcException"/>,
/// <see cref="HttpRequestException"/>); any other error propagates — it is not a transient
/// outage and should not be masked as one.
/// </summary>
public sealed class MachineTelemetryPipeline
{
    private readonly int _maxPending;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryBackoff;
    private readonly ILogger? _logger;
    private readonly ConcurrentQueue<MachineTelemetrySample> _pending = new();
    private long _sent;
    private long _droppedOverflow;
    private long _givenUp;

    public MachineTelemetryPipeline(int maxPending, int maxAttempts, TimeSpan retryBackoff,
        ILogger? logger = null)
    {
        if (maxPending <= 0) throw new ArgumentOutOfRangeException(nameof(maxPending));
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (retryBackoff < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryBackoff));
        _maxPending = maxPending;
        _maxAttempts = maxAttempts;
        _retryBackoff = retryBackoff;
        _logger = logger;
    }

    public int PendingCount => _pending.Count;
    public long SentCount => Interlocked.Read(ref _sent);
    public long DroppedOverflowCount => Interlocked.Read(ref _droppedOverflow);
    public long GivenUpCount => Interlocked.Read(ref _givenUp);

    /// <summary>Buffers a sample; at the cap the oldest buffered sample is dropped so the
    /// newest lands. Never throws — a full buffer must not take the sampling loop down.</summary>
    public void Enqueue(MachineTelemetrySample sample)
    {
        _pending.Enqueue(sample);
        while (_pending.Count > _maxPending && _pending.TryDequeue(out _))
            Interlocked.Increment(ref _droppedOverflow);
    }

    /// <summary>Sends the oldest buffered sample with the bounded retry. True when it was
    /// sent (or nothing was pending); false when it was given up after every attempt. A
    /// cancellation propagates and the sample stays buffered for the flush.</summary>
    public async Task<bool> SendOldestAsync(
        Func<MachineTelemetrySample, CancellationToken, Task> send, CancellationToken token)
    {
        if (!_pending.TryPeek(out var sample)) return true;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                await send(sample, token);
                _pending.TryDequeue(out _);
                Interlocked.Increment(ref _sent);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (error is RpcException or HttpRequestException)
            {
                if (attempt < _maxAttempts)
                    await Task.Delay(_retryBackoff, token);
            }
        }
        _pending.TryDequeue(out _);
        Interlocked.Increment(ref _givenUp);
        _logger?.LogWarning(
            "Machine telemetry sample at {SampledAtUnixMs} given up after {Attempts} attempts; buffer {Pending}",
            sample.SampledAtUnixMs, _maxAttempts, _pending.Count);
        return false;
    }

    /// <summary>Best-effort flush of everything buffered on shutdown: one attempt each, no
    /// backoff. The first failure ends the flush (the rest is dropped and counted) rather
    /// than blocking the exit.</summary>
    public async Task FlushAsync(
        Func<MachineTelemetrySample, CancellationToken, Task> send, CancellationToken token)
    {
        while (_pending.TryPeek(out var sample))
        {
            try
            {
                await send(sample, token);
                _pending.TryDequeue(out _);
                Interlocked.Increment(ref _sent);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _pending.TryDequeue(out _);
                Interlocked.Increment(ref _givenUp);
                break;
            }
        }
    }
}
