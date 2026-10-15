using System.Text.Json;

namespace Chorus.LoadTool;

/// <summary>
/// Produces raw Douyin-shaped payloads that the Douyin mapper accepts
/// (session_id / event_id / user_id / event / gift_*). Event ids are
/// globally unique across the driver (or duplicated by the dup-rate) so
/// dedup is exercised exactly like a retrying platform.
/// </summary>
public sealed class EventPayloadFactory
{
    private readonly Random _random = new();
    private readonly HashSet<string> _recentIds = new(StringComparer.Ordinal);
    private readonly int _userPoolSize;
    private readonly double _giftRate;
    private readonly double _duplicateRate;
    private readonly string? _matchId;
    private readonly string _platform;
    private readonly Func<long>? _sequenceProvider;
    private long _sequence;

    public EventPayloadFactory(string platform, int userPoolSize, double giftRate, double duplicateRate,
        string? matchId, Func<long>? sequenceProvider = null)
    {
        _platform = platform;
        _userPoolSize = Math.Max(1, userPoolSize);
        _giftRate = Math.Clamp(giftRate, 0, 1);
        _duplicateRate = Math.Clamp(duplicateRate, 0, 1);
        _matchId = matchId;
        _sequenceProvider = sequenceProvider;
    }

    public string Create(string sessionId)
    {
        // Event ids are the gateway's dedup key: parallel producers must draw
        // from one shared sequence or two producers can mint the same id and
        // the face swallows the event as a platform retry.
        var seq = _sequenceProvider is { } provide ? provide() : Interlocked.Increment(ref _sequence);
        var userId = "u_" + _random.Next(_userPoolSize);
        var isGift = _random.NextDouble() < _giftRate;

        string eventId;
        lock (_recentIds)
        {
            if (_recentIds.Count > 0 && _random.NextDouble() < _duplicateRate)
            {
                eventId = _recentIds.ElementAt(_random.Next(_recentIds.Count));
            }
            else
            {
                eventId = $"{sessionId}-e{seq}";
                _recentIds.Add(eventId);
                if (_recentIds.Count > 512)
                {
                    _recentIds.Remove(_recentIds.First());
                }
            }
        }

        // "test" platform: TestPlatformEventMapper shape (camelCase), accepted by
        // the AllowAll fallback verifier. Other platforms: Douyin mapper shape.
        var isTest = _platform == "test";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(isTest ? "sessionId" : "session_id", sessionId);
            writer.WriteString(isTest ? "eventId" : "event_id", eventId);
            writer.WriteString(isTest ? "userId" : "user_id", userId);
            writer.WriteString(isTest ? "kind" : "event",
                isGift ? "gift" : (_random.NextDouble() < 0.7 ? "comment" : "like"));
            if (isGift)
            {
                writer.WriteString(isTest ? "giftId" : "gift_id", "g_" + _random.Next(20));
                writer.WriteNumber(isTest ? "giftCount" : "gift_count", 1 + _random.Next(3));
                writer.WriteNumber(isTest ? "giftValue" : "gift_value", 100 * (1 + _random.Next(10)));
            }
            else
            {
                writer.WriteString(isTest ? "text" : "content", "load-" + seq);
            }
            if (_matchId != null)
            {
                writer.WriteString(isTest ? "matchId" : "match_id", _matchId);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
