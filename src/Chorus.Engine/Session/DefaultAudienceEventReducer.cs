using Chorus.Protocol.Models;

namespace Chorus.Engine.Session;

public sealed class DefaultAudienceEventReducer : IAudienceEventReducer
{
    public AudienceEventReduceResult Reduce(IReadOnlyList<AudienceEvent> events, int maxEventsPerTick)
    {
        if (maxEventsPerTick <= 0 || events.Count <= maxEventsPerTick)
        {
            return new AudienceEventReduceResult(events, MergedInputEvents: 0, DroppedInputEvents: 0);
        }

        var likesByUser = new Dictionary<string, int>(StringComparer.Ordinal);
        var giftsByUserGift = new Dictionary<(string UserId, string GiftId), (int Count, int Value)>();
        var comments = new List<AudienceEvent>(capacity: Math.Min(events.Count, maxEventsPerTick));
        var passthrough = new List<AudienceEvent>();
        var likeEventCount = 0;
        var giftEventCount = 0;

        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case AudienceEventKind.Like:
                    likeEventCount++;
                    likesByUser[e.UserId] = likesByUser.TryGetValue(e.UserId, out var c) ? c + 1 : 1;
                    break;
                case AudienceEventKind.Gift:
                    {
                        giftEventCount++;
                        var giftId = e.GiftId ?? "unknown";
                        var giftCount = e.GiftCount ?? 1;
                        var giftValue = e.GiftValue ?? 0;
                        var key = (e.UserId, giftId);
                        if (giftsByUserGift.TryGetValue(key, out var v))
                        {
                            giftsByUserGift[key] = (v.Count + giftCount, v.Value + giftValue * giftCount);
                        }
                        else
                        {
                            giftsByUserGift[key] = (giftCount, giftValue * giftCount);
                        }
                    }
                    break;
                case AudienceEventKind.Comment:
                    comments.Add(e);
                    break;
                default:
                    passthrough.Add(e);
                    break;
            }
        }

        var reduced = new List<AudienceEvent>(capacity: maxEventsPerTick);
        var firstEvent = events[0];
        var mergedInputEvents = Math.Max(0, likeEventCount - likesByUser.Count)
            + Math.Max(0, giftEventCount - giftsByUserGift.Count);

        foreach (var e in passthrough)
        {
            reduced.Add(e);
            if (reduced.Count >= maxEventsPerTick)
            {
                return BuildResult(events.Count, reduced, mergedInputEvents);
            }
        }

        foreach (var kv in giftsByUserGift)
        {
            reduced.Add(new AudienceEvent(
                EventId: $"merged:gift:{kv.Key.UserId}:{kv.Key.GiftId}:{Guid.NewGuid():N}",
                Platform: "merged",
                SessionId: firstEvent.SessionId,
                UserId: kv.Key.UserId,
                Kind: AudienceEventKind.Gift,
                IngestTimeMs: firstEvent.IngestTimeMs,
                GiftId: kv.Key.GiftId,
                GiftCount: kv.Value.Count,
                GiftValue: kv.Value.Value,
                MatchId: firstEvent.MatchId,
                FactionId: firstEvent.FactionId
            ));
            if (reduced.Count >= maxEventsPerTick)
            {
                return BuildResult(events.Count, reduced, mergedInputEvents);
            }
        }

        foreach (var kv in likesByUser)
        {
            reduced.Add(new AudienceEvent(
                EventId: $"merged:like:{kv.Key}:{Guid.NewGuid():N}",
                Platform: "merged",
                SessionId: firstEvent.SessionId,
                UserId: kv.Key,
                Kind: AudienceEventKind.Like,
                IngestTimeMs: firstEvent.IngestTimeMs,
                Text: kv.Value.ToString(),
                MatchId: firstEvent.MatchId,
                FactionId: firstEvent.FactionId
            ));
            if (reduced.Count >= maxEventsPerTick)
            {
                return BuildResult(events.Count, reduced, mergedInputEvents);
            }
        }

        foreach (var e in comments)
        {
            reduced.Add(e);
            if (reduced.Count >= maxEventsPerTick)
            {
                return BuildResult(events.Count, reduced, mergedInputEvents);
            }
        }

        return BuildResult(events.Count, reduced, mergedInputEvents);
    }

    private static AudienceEventReduceResult BuildResult(int inputCount, IReadOnlyList<AudienceEvent> reduced, int mergedInputEvents)
    {
        var droppedInputEvents = Math.Max(0, inputCount - mergedInputEvents - reduced.Count);
        return new AudienceEventReduceResult(reduced, mergedInputEvents, droppedInputEvents);
    }
}
