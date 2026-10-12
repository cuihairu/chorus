using Chorus.Protocol.Models;

namespace Chorus.SampleGame;

internal sealed class MatchStateStore
{
    private sealed class MatchState
    {
        public required string MatchId { get; init; }
        public HashSet<string> SessionIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FactionState> Factions { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FactionState
    {
        public required string FactionId { get; init; }
        public long Likes { get; set; }
        public long Gifts { get; set; }
        public long Comments { get; set; }
        public HashSet<string> Users { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SessionIds { get; } = new(StringComparer.Ordinal);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, MatchState> _matches = new(StringComparer.Ordinal);

    public void Apply(string sessionId, AudienceEvent e, int likes, int gifts, int comments)
    {
        if (string.IsNullOrWhiteSpace(e.MatchId) || string.IsNullOrWhiteSpace(e.FactionId))
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

            match.SessionIds.Add(sessionId);

            if (!match.Factions.TryGetValue(e.FactionId, out var faction))
            {
                faction = new FactionState
                {
                    FactionId = e.FactionId,
                };
                match.Factions[e.FactionId] = faction;
            }

            faction.Users.Add(e.UserId);
            faction.SessionIds.Add(sessionId);
            faction.Likes += likes;
            faction.Gifts += gifts;
            faction.Comments += comments;
        }
    }

    public MatchStateSnapshot? GetSnapshot(string? matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            return null;
        }

        lock (_lock)
        {
            if (!_matches.TryGetValue(matchId, out var match))
            {
                return null;
            }

            return new MatchStateSnapshot(
                MatchId: match.MatchId,
                SessionIds: match.SessionIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                Factions: match.Factions.Values
                    .OrderBy(f => f.FactionId, StringComparer.Ordinal)
                    .Select(f => new FactionStateSnapshot(
                        FactionId: f.FactionId,
                        SessionIds: f.SessionIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                        UserCount: f.Users.Count,
                        Likes: f.Likes,
                        Gifts: f.Gifts,
                        Comments: f.Comments))
                    .ToArray());
        }
    }
}
