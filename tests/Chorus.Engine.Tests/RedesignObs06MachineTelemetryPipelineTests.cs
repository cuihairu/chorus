using Chorus.NodeAgent;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The machine telemetry reliability core (FIX-OBS-06): the bounded buffer keeps the newest
/// sample and drops the oldest at the cap, the per-sample retry is bounded (a long outage
/// gives the sample up instead of retrying forever), the flush on shutdown drains without
/// backoff, and a cancellation keeps the sample buffered rather than losing it.
/// </summary>
public sealed class RedesignObs06MachineTelemetryPipelineTests
{
    private static MachineTelemetrySample Sample(long at) => new(at, null, null, null, null, null);

    [Fact]
    public async Task SendsTheOldestBufferedSample()
    {
        var pipeline = new MachineTelemetryPipeline(maxPending: 5, maxAttempts: 3, retryBackoff: TimeSpan.Zero);
        pipeline.Enqueue(Sample(1_000));
        pipeline.Enqueue(Sample(2_000));
        var sent = new List<long>();
        var ok = await pipeline.SendOldestAsync((s, _) =>
        {
            sent.Add(s.SampledAtUnixMs);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal([1_000L], sent); // the oldest, first in, first out
        Assert.Equal(1, pipeline.PendingCount);
        Assert.Equal(1, pipeline.SentCount);
        Assert.Equal(0, pipeline.GivenUpCount);
    }

    [Fact]
    public async Task RetriesBoundedThenGivesTheSampleUp()
    {
        var pipeline = new MachineTelemetryPipeline(maxPending: 5, maxAttempts: 3, retryBackoff: TimeSpan.Zero);
        pipeline.Enqueue(Sample(1_000));
        var attempts = 0;
        Task Down(MachineTelemetrySample _, CancellationToken __)
        {
            attempts++;
            throw new RpcException(new Status(StatusCode.Unavailable, "control plane down"));
        }

        var ok = await pipeline.SendOldestAsync(Down, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(3, attempts); // exactly the bounded budget, no more
        Assert.Equal(0, pipeline.PendingCount);
        Assert.Equal(1, pipeline.GivenUpCount);
        Assert.Equal(0, pipeline.SentCount);
    }

    [Fact]
    public async Task RetriesUntilTheTransportRecovers()
    {
        var pipeline = new MachineTelemetryPipeline(maxPending: 5, maxAttempts: 5, retryBackoff: TimeSpan.Zero);
        pipeline.Enqueue(Sample(1_000));
        var attempts = 0;
        async Task Flaky(MachineTelemetrySample _, CancellationToken __)
        {
            attempts++;
            if (attempts <= 2) throw new RpcException(new Status(StatusCode.Unavailable, "down"));
        }

        var ok = await pipeline.SendOldestAsync(Flaky, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(3, attempts); // two failures, third succeeds
        Assert.Equal(0, pipeline.PendingCount);
        Assert.Equal(1, pipeline.SentCount);
        Assert.Equal(0, pipeline.GivenUpCount);
    }

    [Fact]
    public async Task DropsTheOldestWhenTheBufferIsFull()
    {
        var pipeline = new MachineTelemetryPipeline(maxPending: 2, maxAttempts: 3, retryBackoff: TimeSpan.Zero);
        pipeline.Enqueue(Sample(1_000));
        pipeline.Enqueue(Sample(2_000));
        pipeline.Enqueue(Sample(3_000)); // cap is 2 → the 1_000 sample is dropped

        Assert.Equal(2, pipeline.PendingCount);
        Assert.Equal(1, pipeline.DroppedOverflowCount);
        Assert.Equal(0, pipeline.GivenUpCount); // the drop was the buffer's cap, not a retry give-up
        // The newest still lands; one drain confirms the oldest survivor is 2_000 — 1_000 was
        // dropped by the cap, and the next drain would carry 3_000.
        var sent = new List<long>();
        await pipeline.SendOldestAsync((s, _) =>
        {
            sent.Add(s.SampledAtUnixMs);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal([2_000L], sent);
        Assert.Equal(1, pipeline.PendingCount);
    }

    [Fact]
    public async Task FlushDrainsTheBufferWithoutRetry()
    {
        var pipeline = new MachineTelemetryPipeline(maxPending: 5, maxAttempts: 3, retryBackoff: TimeSpan.Zero);
        pipeline.Enqueue(Sample(1_000));
        pipeline.Enqueue(Sample(2_000));
        var sent = new List<long>();
        await pipeline.FlushAsync((s, _) =>
        {
            sent.Add(s.SampledAtUnixMs);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal([1_000L, 2_000L], sent);
        Assert.Equal(0, pipeline.PendingCount);
        Assert.Equal(2, pipeline.SentCount);
        Assert.Equal(0, pipeline.GivenUpCount);
    }

    [Fact]
    public async Task FlushStopsAtTheFirstFailure()
    {
        var pipeline = new MachineTelemetryPipeline(maxPending: 5, maxAttempts: 3, retryBackoff: TimeSpan.Zero);
        pipeline.Enqueue(Sample(1_000));
        pipeline.Enqueue(Sample(2_000));
        await pipeline.FlushAsync((_, _) =>
            throw new RpcException(new Status(StatusCode.Unavailable, "down")), CancellationToken.None);

        // The flush gives up after the first failure instead of blocking the exit on backoff:
        // the failed sample is dropped and the rest stay buffered (lost with the process on
        // exit — the flush is best-effort, never a reason to hold the shutdown).
        Assert.Equal(1, pipeline.PendingCount);
        Assert.Equal(1, pipeline.GivenUpCount);
        Assert.Equal(0, pipeline.SentCount);
    }

    [Fact]
    public async Task CancellationKeepsTheSampleBufferedForTheFlush()
    {
        var pipeline = new MachineTelemetryPipeline(maxPending: 5, maxAttempts: 3, retryBackoff: TimeSpan.Zero);
        pipeline.Enqueue(Sample(1_000));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pipeline.SendOldestAsync(
            (_, __) => throw new OperationCanceledException(), cts.Token));

        Assert.Equal(1, pipeline.PendingCount); // the cancelled sample is still buffered
        Assert.Equal(0, pipeline.GivenUpCount);
    }
}
