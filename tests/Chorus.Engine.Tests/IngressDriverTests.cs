using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
        private readonly ConcurrentQueue<string> _eventIds = new();
        public string[] SessionIds => _sessionIds.ToArray();
        public string[] EventIds => _eventIds.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            _sessionIds.Enqueue(doc.RootElement.GetProperty("sessionId").GetString()!);
            if (doc.RootElement.TryGetProperty("eventId", out var eventId))
            {
                _eventIds.Enqueue(eventId.GetString()!);
            }

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

    /// <summary>Records each post's raw body and signature header without
    /// parsing the payload, so douyin-shaped bodies work too.</summary>
    private sealed class RecordingHandler(ConcurrentQueue<(string Body, string? Signature)> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            request.Headers.TryGetValues(IngressSigner.DouyinSignatureHeader, out var signatures);
            seen.Enqueue((body, signatures?.FirstOrDefault()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Plain) });
        }
    }

    private static LoadToolOptions Options(int sessions = 1, int producers = 1, int rate = 200)
        => new(BaseUrl: "http://stub", Platform: "test", Sessions: sessions,
            EventsPerSecondTotal: rate, ProducerConnections: producers);

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

    [Fact]
    public async Task SignSecret_SignsEveryPostWithDouyinScheme()
    {
        const string secret = "loadtool-signing-key";
        var seen = new ConcurrentQueue<(string Body, string? Signature)>();
        var driver = new IngressDriver(new HttpClient(new RecordingHandler(seen)), "http://stub", "douyin",
            Options() with { SignSecret = secret });

        using var cts = new CancellationTokenSource();
        await RunUntilAsync(driver, () => driver.EventsSent >= 3, cts);

        Assert.True(seen.Count >= 3, $"only {seen.Count} reached the stub");
        // Independent assertion of the documented scheme (not a round-trip
        // through IngressSigner): the header must be HMAC-SHA256 of the exact
        // raw body the driver posts, as lowercase hex.
        foreach (var (body, signature) in seen)
        {
            Assert.NotNull(signature);
            var expected = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            Assert.Equal(expected, signature);
            Assert.Equal(64, signature!.Length);
        }
    }

    [Fact]
    public async Task NoSignSecret_NoSignatureHeaderOnPosts()
    {
        var seen = new ConcurrentQueue<(string Body, string? Signature)>();
        var driver = new IngressDriver(new HttpClient(new RecordingHandler(seen)), "http://stub", "douyin", Options());

        using var cts = new CancellationTokenSource();
        await RunUntilAsync(driver, () => driver.EventsSent >= 3, cts);

        Assert.True(seen.Count >= 3, $"only {seen.Count} reached the stub");
        Assert.All(seen, item => Assert.Null(item.Signature));
    }

    [Fact]
    public async Task FailClosed401s_CountAsUnauthorized_NotOther()
    {
        // The 2026-10-15 run burned its whole duration on pure 401s that hid
        // inside other=: wrong-or-missing-signature is a different failure
        // from a 500 or a transport error and must be readable on its own.
        var handler = new StubHandler(_ => (HttpStatusCode.Unauthorized, "{}"));
        var driver = new IngressDriver(new HttpClient(handler), "http://stub", "test", Options());

        using var cts = new CancellationTokenSource();
        await RunUntilAsync(driver, () => driver.EventsSent >= 5, cts);

        Assert.True(driver.EventsSent >= 5, $"sent={driver.EventsSent}");
        Assert.Equal(driver.EventsSent, driver.Unauthorized);
        Assert.Equal(0, driver.Accepted);
        Assert.Equal(0, driver.Other);
        Assert.Equal(0, driver.RateLimited);
    }

    [Fact]
    public async Task ParallelProducers_NeverCollideEventIds()
    {
        // 2026-10-15 gate reproduction run: 97% of sent events came back
        // "duplicated" because each producer seeded its session cursor from
        // Environment.CurrentManagedThreadId and numbered its event ids from
        // its own per-factory counter - two producers starting on the same
        // pool thread (routine under the WS-client load the tool itself
        // generates) emit identical (room, seq) streams and the dedup face
        // swallows them as platform retries. Event ids are the dedup key:
        // with dup-rate 0 they must be globally unique across producers even
        // in the worst case of a single shared session.
        var handler = new StubHandler(_ => (HttpStatusCode.OK, Plain));
        var driver = new IngressDriver(new HttpClient(handler), "http://stub", "test",
            Options(sessions: 1, producers: 8, rate: 200) with { DuplicateRate = 0 });

        using var cts = new CancellationTokenSource();
        await RunUntilAsync(driver, () => driver.EventsSent >= 40, cts);

        Assert.True(handler.EventIds.Length >= 40, $"only {handler.EventIds.Length} reached the stub");
        Assert.Equal(handler.EventIds.Length, handler.EventIds.Distinct().Count());
    }
}
