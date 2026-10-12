using Chorus.Protocol.Models;

namespace Chorus.Engine.Session;

/// <summary>
/// Merges audience events when a tick exceeds its event budget.
///
/// GROUPING (review P0-3): likes group by (SessionId, MatchId, FactionId,
/// UserId) and gifts by (SessionId, MatchId, FactionId, UserId, GiftId) so
/// events from different factions/matches/sessions can never merge into each
/// other - a merged event carries its own group's context, never the context
/// of some unrelated first event.
///
/// COUNT SEMANTICS: a merged like event carries the source event count in
/// <see cref="AudienceEvent.Text"/> (parsed by game modules); a merged gift
/// event carries the summed GiftCount and the summed total value in
/// GiftValue. Source-count provenance per faction is therefore preserved and
/// per-faction totals stay conserved across the merge.
/// </summary>
public sealed class DefaultAudienceEventReducer : IAudienceEventReducer
{
    public AudienceEventReduceResult Reduce(IReadOnlyList<AudienceEvent> events, int maxEventsPerTick)
    {
        if (maxEventsPerTick <= 0 || events.Count <= maxEventsPerTick)
        {
            return new AudienceEventReduceResult(events, MergedInputEvents: 0, DroppedInputEvents: 0);
        }

        // Per-group accumulators: firstIngestMs preserves a meaningful ordering hint.
        var likesByGroup = new Dictionary<(string SessionId, string MatchId, string FactionId, string UserId), (int Count, long FirstIngestMs)>();
        var giftsByGroup = new Dictionary<(string SessionId, string MatchId, string FactionId, string UserId, string GiftId), (int Count, int Value, long FirstIngestMs)>();
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
                    {
                        var key = (e.SessionId, e.MatchId ?? "", e.FactionId ?? "", e.UserId);
                        if (likesByGroup.TryGetValue(key, out var v))
                        {
                            likesByGroup[key] = (v.Count + 1, v.FirstIngestMs);
                        }
                        else
                        {
                            likesByGroup[key] = (1, e.IngestTimeMs);
                        }
                    }
                    break;
                case AudienceEventKind.Gift:
                    {
                        giftEventCount++;
                        var giftId = e.GiftId ?? "unknown";
                        var giftCount = e.GiftCount ?? 1;
                        var giftValue = e.GiftValue ?? 0;
                        var key = (e.SessionId, e.MatchId ?? "", e.FactionId ?? "", e.UserId, giftId);
                        if (giftsByGroup.TryGetValue(key, out var v))
                        {
                            giftsByGroup[key] = (v.Count + giftCount, v.Value + giftValue * giftCount, v.FirstIngestMs);
                        }
                        else
                        {
                            giftsByGroup[key] = (giftCount, giftValue * giftCount, e.IngestTimeMs);
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
        var mergedInputEvents = Math.Max(0, likeEventCount - likesByGroup.Count)
            + Math.Max(0, giftEventCount - giftsByGroup.Count);

        foreach (var e in passthrough)
        {
            reduced.Add(e);
            if (reduced.Count >= maxEventsPerTick)
            {
                return BuildResult(events.Count, reduced, mergedInputEvents);
            }
        }

        foreach (var kv in giftsByGroup)
        {
            reduced.Add(new AudienceEvent(
                EventId: $"merged:gift:{kv.Key.SessionId}:{kv.Key.MatchId}:{kv.Key.FactionId}:{kv.Key.UserId}:{kv.Key.GiftId}:{Guid.NewGuid():N}",
                Platform: "merged",
                SessionId: kv.Key.SessionId,
                UserId: kv.Key.UserId,
                Kind: AudienceEventKind.Gift,
                IngestTimeMs: kv.Value.FirstIngestMs,
                GiftId: kv.Key.GiftId,
                GiftCount: kv.Value.Count,
                GiftValue: kv.Value.Value,
                MatchId: NullIfEmpty(kv.Key.MatchId),
                FactionId: NullIfEmpty(kv.Key.FactionId)
            ));
            if (reduced.Count >= maxEventsPerTick)
            {
                return BuildResult(events.Count, reduced, mergedInputEvents);
            }
        }

        foreach (var kv in likesByGroup)
        {
            reduced.Add(new AudienceEvent(
                EventId: $"merged:like:{kv.Key.SessionId}:{kv.Key.MatchId}:{kv.Key.FactionId}:{kv.Key.UserId}:{Guid.NewGuid():N}",
                Platform: "merged",
                SessionId: kv.Key.SessionId,
                UserId: kv.Key.UserId,
                Kind: AudienceEventKind.Like,
                IngestTimeMs: kv.Value.FirstIngestMs,
                Text: kv.Value.Count.ToString(),
                MatchId: NullIfEmpty(kv.Key.MatchId),
                FactionId: NullIfEmpty(kv.Key.FactionId)
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

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static AudienceEventReduceResult BuildResult(int inputCount, IReadOnlyList<AudienceEvent> reduced, int mergedInputEvents)
    {
        var droppedInputEvents = Math.Max(0, inputCount - mergedInputEvents - reduced.Count);
        return new AudienceEventReduceResult(reduced, mergedInputEvents, droppedInputEvents);
    }
}
