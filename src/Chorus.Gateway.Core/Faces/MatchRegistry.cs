using Chorus.Protocol.Models;

namespace Chorus.Gateway.Core.Faces;

public sealed class MatchRegistry
{
    private sealed class MatchState
    {
        public required string MatchId { get; init; }
        public HashSet<string> SessionIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> SessionIdsByFaction { get; } = new(StringComparer.Ordinal);
        public long TotalEvents { get; set; }
        public long LastEventTimeMs { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, MatchState> _matches = new(StringComparer.Ordinal);

    public void Track(AudienceEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.MatchId))
        {
            return;
        }

        lock (_lock)
        {
            if (!_matches.TryGetValue(e.MatchId, out var match))
            {
                match = new MatchState
                {
                    MatchId = e.MatchId,
                };
                _matches[e.MatchId] = match;
            }

            match.SessionIds.Add(e.SessionId);
            if (!string.IsNullOrWhiteSpace(e.FactionId))
            {
                if (!match.SessionIdsByFaction.TryGetValue(e.FactionId, out var sessionIds))
                {
                    sessionIds = new HashSet<string>(StringComparer.Ordinal);
                    match.SessionIdsByFaction[e.FactionId] = sessionIds;
                }

                sessionIds.Add(e.SessionId);
            }

            match.TotalEvents++;
            match.LastEventTimeMs = e.IngestTimeMs;
        }
    }

    public IReadOnlyList<MatchDiagnostics> GetDiagnostics()
    {
        lock (_lock)
        {
            return _matches.Values
                .OrderBy(m => m.MatchId, StringComparer.Ordinal)
                .Select(m => new MatchDiagnostics(
                    Match: new MatchStateSnapshot(
                        MatchId: m.MatchId,
                        SessionIds: m.SessionIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                        Factions: m.SessionIdsByFaction
                            .OrderBy(x => x.Key, StringComparer.Ordinal)
                            .Select(x => new FactionStateSnapshot(
                                FactionId: x.Key,
                                SessionIds: x.Value.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                                UserCount: 0,
                                Likes: 0,
                                Gifts: 0,
                                Comments: 0))
                            .ToArray()),
                    TotalEvents: m.TotalEvents,
                    LastEventTimeMs: m.LastEventTimeMs))
                .ToArray();
        }
    }
}

public sealed record MatchDiagnostics(
    MatchStateSnapshot Match,
    long TotalEvents,
    long LastEventTimeMs
);
