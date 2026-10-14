using System.Net.Http.Headers;

namespace Chorus.LoadTool;

/// <summary>
/// Sends audience events to /ingress/{platform} at a fixed rate per session
/// across several parallel producers. Counts by HTTP status: 200 (accepted),
/// 200+duplicated, 429 (rate limited), 503 (event lane full).
/// </summary>
public sealed class IngressDriver
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _platform;
    private readonly LoadToolOptions _options;
    private readonly string[] _sessions;

    public long EventsSent => Interlocked.Read(ref _sent);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Duplicated => Interlocked.Read(ref _duplicated);
    public long RateLimited => Interlocked.Read(ref _rateLimited);
    public long LaneFull => Interlocked.Read(ref _laneFull);
    public long Other => Interlocked.Read(ref _other);

    private long _sent, _accepted, _duplicated, _rateLimited, _laneFull, _other;

    public IngressDriver(HttpClient http, string baseUrl, string platform, LoadToolOptions options)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _platform = platform;
        _options = options;
        _sessions = Enumerable.Range(1, Math.Max(1, options.Sessions))
            .Select(n => $"load-s{n}")
            .ToArray();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var producers = Enumerable.Range(1, Math.Max(1, _options.ProducerConnections))
            .Select(n => Task.Run(() => ProduceAsync(cancellationToken), cancellationToken))
            .ToArray();

        await Task.WhenAll(producers);
    }

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        // Each producer owns its factory: Random is not shared across threads.
        var factory = new EventPayloadFactory(
            _platform, _options.UserPoolSize, _options.GiftRate, _options.DuplicateRate, _options.MatchId);

        var perSessionRps = Math.Max(1, _options.EventsPerSecondPerSession);
        var producerRps = Math.Max(1, (int)Math.Ceiling(perSessionRps / (double)Math.Max(1, _options.ProducerConnections)));
        var intervalMs = 1000 / producerRps;
        var index = Environment.CurrentManagedThreadId % Math.Max(1, _sessions.Length);

        while (!cancellationToken.IsCancellationRequested)
        {
            var sessionId = _sessions[index++ % _sessions.Length];
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/ingress/{_platform}");
            request.Content = new StringContent(factory.Create(sessionId), System.Text.Encoding.UTF8, "application/json");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                switch ((int)response.StatusCode)
                {
                    case 200:
                        Interlocked.Increment(ref _accepted);
                        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        if (body.Contains("\"duplicated\":true", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref _duplicated);
                        }
                        break;
                    case 429:
                        Interlocked.Increment(ref _rateLimited);
                        break;
                    case 503:
                        Interlocked.Increment(ref _laneFull);
                        break;
                    default:
                        Interlocked.Increment(ref _other);
                        break;
                }
                Interlocked.Increment(ref _sent);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _other);
                Interlocked.Increment(ref _sent);
            }

            var elapsed = (int)stopwatch.ElapsedMilliseconds;
            if (elapsed < intervalMs)
            {
                await Task.Delay(intervalMs - elapsed, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
