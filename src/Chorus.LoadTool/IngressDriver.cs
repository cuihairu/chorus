using System.Net.Http.Headers;

namespace Chorus.LoadTool;

/// <summary>
/// Sends audience events to /ingress/{platform} at a fixed total rate
/// (spread across parallel producers and round-robined over all sessions).
/// Counts by HTTP status: 200 (accepted), 200+duplicated, 401 (unverified
/// signature), 429 (rate limited), 503 (event lane full).
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
    public long Unauthorized => Interlocked.Read(ref _unauthorized);
    public long Other => Interlocked.Read(ref _other);

    private long _sent, _accepted, _duplicated, _rateLimited, _laneFull, _unauthorized, _other;

    /// <summary>Event ids are the dedup key: all producers draw from one
    /// sequence so no two producers can mint the same id.</summary>
    private long _sequence;

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
        var producers = Enumerable.Range(0, Math.Max(1, _options.ProducerConnections))
            .Select(n => Task.Run(() => ProduceAsync(n, cancellationToken), cancellationToken))
            .ToArray();

        await Task.WhenAll(producers);
    }

    private async Task ProduceAsync(int ordinal, CancellationToken cancellationToken)
    {
        // Each producer owns its factory (Random is not shared across threads)
        // but draws event ids from the driver-wide sequence.
        var factory = new EventPayloadFactory(
            _platform, _options.UserPoolSize, _options.GiftRate, _options.DuplicateRate, _options.MatchId,
            sequenceProvider: () => Interlocked.Increment(ref _sequence));

        var totalRps = Math.Max(1, _options.EventsPerSecondTotal);
        var producerRps = Math.Max(1, (int)Math.Ceiling(totalRps / (double)Math.Max(1, _options.ProducerConnections)));
        // Absolute-schedule pacing: integer ms division truncated to 0 above
        // ~1000/s per producer and silently disabled pacing (RatePacer keeps
        // the fractional schedule; a producer behind its schedule catches up
        // instead of accumulating idle time).
        var pacer = new RatePacer(producerRps);
        // Seed the session cursor by producer ordinal, not thread id: pool
        // threads are reused under load, and two producers starting on the
        // same thread would pace the same rooms at the same instants.
        var index = ordinal % Math.Max(1, _sessions.Length);

        while (!cancellationToken.IsCancellationRequested)
        {
            var sendDelay = pacer.NextDelay();
            if (sendDelay > TimeSpan.Zero)
            {
                await Task.Delay(sendDelay, cancellationToken).ConfigureAwait(false);
            }

            var sessionId = _sessions[index++ % _sessions.Length];
            var payload = factory.Create(sessionId);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/ingress/{_platform}");
            request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var signature = IngressSigner.Sign(_platform, payload, _options.SignSecret);
            if (signature is not null)
            {
                // Sign the exact body bytes that go on the wire: the gateway
                // hashes the raw body it reads back.
                request.Headers.TryAddWithoutValidation(signature.Value.Name, signature.Value.Value);
            }

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
                    case 401:
                        // Fail-closed signature rejection: a different failure
                        // from a 500 or a transport error - wrong or missing
                        // signature - and it must not hide inside other=.
                        Interlocked.Increment(ref _unauthorized);
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

            pacer.RecordSend();
        }
    }
}
