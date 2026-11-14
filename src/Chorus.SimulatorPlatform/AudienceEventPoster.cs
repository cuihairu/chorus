using System.Net;
using System.Text;
using System.Text.Json;

namespace Chorus.SimulatorPlatform;

/// <summary>One posted event's answer, from the ingress receipt.</summary>
public sealed record PostedReceipt(HttpStatusCode Status, string? Disposition, bool Duplicated);

/// <summary>The poster's running outcome counts (BOTS-2's per-kind send-rate face reads
/// these through the fleet snapshot).</summary>
public sealed record PosterSnapshot(
    IReadOnlyDictionary<ScriptEventKind, long> SentByKind,
    long Accepted, long Duplicated, long Unauthorized, long RateLimited, long Other);

/// <summary>
/// Posts audience events to the platform ingress face as HTTP callbacks (BOTS-1 discipline:
/// bots never bypass the face — every event rides the real verifier/mapper/dedup chain).
/// The payload shape is the simulator mapper's (camelCase sessionId/eventId/kind/...);
/// the event ids draw from one shared sequence, because event ids are the gateway's dedup
/// key and parallel posters must never mint the same id.
/// </summary>
public sealed class AudienceEventPoster
{
    /// <summary>The app-id header the ingress face binds to the credential. Pinned against
    /// the gateway's own constant by a test (parity without a heavy project reference).</summary>
    public const string AppIdHeader = "X-Chorus-App-Id";

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _sessionId;
    private readonly Func<long>? _sequenceProvider;
    private readonly string _eventIdPrefix;
    private readonly long[] _sentByKind = new long[8];
    private long _accepted, _duplicated, _unauthorized, _rateLimited, _other;
    private long _sequence;

    public AudienceEventPoster(HttpClient http, string baseUrl, string sessionId,
        string? eventIdPrefix = null, Func<long>? sequenceProvider = null)
    {
        _http = http;
        _url = $"{baseUrl.TrimEnd('/')}/ingress/simulator";
        _sessionId = sessionId;
        _eventIdPrefix = eventIdPrefix ?? "";
        _sequenceProvider = sequenceProvider;
    }

    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>Posts one event and counts the answer by disposition. Returns the receipt;
    /// transport failures count as Other and surface in the snapshot, they do not throw.</summary>
    public async Task<PostedReceipt> PostAsync(ScheduledBotEvent evt, CancellationToken cancellationToken = default)
    {
        var sequence = _sequenceProvider is { } provide ? provide() : Interlocked.Increment(ref _sequence);
        var eventId = $"{_eventIdPrefix}bot-{sequence}";
        Interlocked.Increment(ref _sentByKind[(int)evt.Kind & 7]);

        using var content = new StringContent(Json(evt, eventId), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
        request.Headers.Add(AppIdHeader, "app-1");
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var receipt = Receipt(response.StatusCode, body);
            switch ((int)response.StatusCode)
            {
                case 200 when receipt.Duplicated:
                    Interlocked.Increment(ref _duplicated);
                    break;
                case 200:
                    Interlocked.Increment(ref _accepted);
                    break;
                case 401:
                    Interlocked.Increment(ref _unauthorized);
                    break;
                case 429:
                    Interlocked.Increment(ref _rateLimited);
                    break;
                default:
                    Interlocked.Increment(ref _other);
                    break;
            }
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _other);
            throw;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _other);
            return new PostedReceipt(0, null, false);
        }
    }

    public PosterSnapshot Snapshot()
    {
        var byKind = new Dictionary<ScriptEventKind, long>();
        foreach (ScriptEventKind kind in Enum.GetValues<ScriptEventKind>())
            byKind[kind] = Interlocked.Read(ref _sentByKind[(int)kind & 7]);
        return new PosterSnapshot(byKind,
            Interlocked.Read(ref _accepted), Interlocked.Read(ref _duplicated),
            Interlocked.Read(ref _unauthorized), Interlocked.Read(ref _rateLimited),
            Interlocked.Read(ref _other));
    }

    /// <summary>The simulator mapper's payload shape: camelCase fields, the kind named by
    /// its enum name (the mapper parses names case-insensitively), unit gift counts. The
    /// session id names this poster's live session — the attribution key.</summary>
    internal string Json(ScheduledBotEvent evt, string eventId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", _sessionId);
            writer.WriteString("eventId", eventId);
            writer.WriteString("kind", evt.Kind.ToString());
            writer.WriteString("userId", evt.UserId);
            if (evt.Text is { } text) writer.WriteString("text", text);
            if (evt.GiftId is { } giftId && evt.GiftValue > 0)
            {
                writer.WriteString("giftId", giftId);
                writer.WriteNumber("giftCount", 1);
                writer.WriteNumber("giftValue", evt.GiftValue);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static PostedReceipt Receipt(HttpStatusCode status, string body)
    {
        string? disposition = null;
        var duplicated = false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("receipts", out var receipts)
                && receipts.ValueKind == JsonValueKind.Array)
                foreach (var receipt in receipts.EnumerateArray())
                {
                    if (receipt.TryGetProperty("disposition", out var element)
                        && element.ValueKind == JsonValueKind.String)
                        disposition = element.GetString();
                    if (receipt.TryGetProperty("duplicated", out var duplicate)
                        && duplicate.ValueKind == JsonValueKind.True)
                        duplicated = true;
                }
        }
        catch (JsonException)
        {
            // A non-JSON error body still answers by status; the receipt stays null-backed.
        }
        return new PostedReceipt(status, disposition, duplicated);
    }
}
