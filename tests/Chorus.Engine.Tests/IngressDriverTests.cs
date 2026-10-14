using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Ingress driver unit coverage: HTTP status buckets (200 / duplicated / 429 /
/// 503 / other), producer failures counted as other, and session round-robin
/// across parallel producers - counters must stay exact under concurrency.
/// </summary>
public sealed class IngressDriverTests
{
    /// <summary>Stands in for the real ingress endpoint: returns status+body by
    /// request index (cycling) and records each payload's session id.</summary>
    private sealed class StubHandler(Func<int, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        // Producers hit the stub concurrently: the record must be thread-safe.
        private readonly ConcurrentQueue<string> _sessionIds = new();
        public string[] SessionIds => _sessionIds.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            _sessionIds.Enqueue(doc.RootElement.GetProperty("sessionId").GetString()!);

            var (status, content) = respond(Interlocked.Increment(ref _cursor));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
        }

        private int _cursor = -1;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw new HttpRequestException("boom");
        }

        private int _attempts;
    }

    private static LoadToolOptions Options(int sessions = 1, int producers = 1, int rate = 200)
        => new(BaseUrl: "http://stub", Platform: "test", Sessions: sessions,
            EventsPerSecondPerSession: rate, ProducerConnections: producers);

    private static async Task RunUntilAsync(IngressDriver driver, Func<bool> done, CancellationTokenSource cts)
    {
        var run = driver.RunAsync(cts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < 10000)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private const string Dup = """{"duplicated":true}""";
    private const string Plain = """{"duplicated":false}""";

    [Fact]
    public async Task StatusBuckets_CountExactly()
    {
        // Cycling 200 / 200-dup / 429 / 503 / 500: whatever number of sends
        // completes before the cancel, every bucket must match what the stub
        // actually served - the counters are the tool's output, no lost updates.
        var handler = new StubHandler(i => ((i % 5) switch
        {
            0 => (HttpStatusCode.OK, Plain),
            1 => (HttpStatusCode.OK, Dup),
            2 => (HttpStatusCode.TooManyRequests, "{}"),
            3 => (HttpStatusCode.ServiceUnavailable, "{}"),
            _ => (HttpStatusCode.InternalServerError, "{}"),
        }));
        var driver = new IngressDriver(new HttpClient(handler), "http://stub/", "test", Options());

        using var cts = new CancellationTokenSource();
        await RunUntilAsync(driver, () => driver.EventsSent >= 10, cts);

        // Completed sends are a prefix of the served cycle (one sequential
        // producer, at most one cancelled in-flight at the end), so each of
        // the five buckets fired at least twice; the buckets must sum to the
        // sent total, and the stub saw every completed send plus that one.
        Assert.True(handler.SessionIds.Length >= 10, $"only {handler.SessionIds.Length} reached the stub");
        Assert.True(driver.EventsSent >= 10, $"sent={driver.EventsSent}");
        Assert.True(driver.Accepted >= 2, $"accepted={driver.Accepted}");
        Assert.True(driver.Duplicated >= 2, $"duplicated={driver.Duplicated}");
        Assert.True(driver.RateLimited >= 2, $"rateLimited={driver.RateLimited}");
        Assert.True(driver.LaneFull >= 2, $"laneFull={driver.LaneFull}");
        Assert.True(driver.Other >= 2, $"other={driver.Other}");
        Assert.Equal(driver.EventsSent, driver.Accepted + driver.RateLimited + driver.LaneFull + driver.Other);
        Assert.InRange(handler.SessionIds.Length, driver.EventsSent, driver.EventsSent + 1);
    }

    [Fact]
    public async Task SendFailures_CountAsOther_AndStillCountAsSent()
    {
        var handler = new ThrowingHandler();
        var driver = new IngressDriver(new HttpClient(handler), "http://stub", "test", Options());

        using var cts = new CancellationTokenSource();
        await RunUntilAsync(driver, () => driver.EventsSent >= 5, cts);

        Assert.True(driver.EventsSent >= 5, $"sent={driver.EventsSent}");
        // Every completed send failed: other tracks sent one-to-one; the stub
        // saw every completed attempt plus at most one cancelled in-flight.
        Assert.Equal(driver.EventsSent, driver.Other);
        Assert.Equal(0, driver.Accepted);
        Assert.InRange(handler.Attempts, driver.EventsSent, driver.EventsSent + 1);
    }

    [Fact]
    public async Task Sessions_RoundRobinAcrossProducers()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, Plain));
        var driver = new IngressDriver(new HttpClient(handler), "http://stub", "test",
            Options(sessions: 2, producers: 2));

        using var cts = new CancellationTokenSource();
        await RunUntilAsync(driver, () => driver.EventsSent >= 8, cts);

        Assert.Contains("load-s1", handler.SessionIds);
        Assert.Contains("load-s2", handler.SessionIds);
        Assert.True(driver.EventsSent >= 8, $"sent={driver.EventsSent}");
        // Every completed send was a 200: accepted tracks sent one-to-one
        // (no lost updates under parallel producers), and the stub saw each
        // completed send plus at most one cancelled in-flight per producer.
        Assert.Equal(driver.EventsSent, driver.Accepted);
        Assert.InRange(handler.SessionIds.Length, driver.EventsSent, driver.EventsSent + 2);
    }
}
