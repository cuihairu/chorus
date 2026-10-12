namespace Chorus.Protocol.Models;

public sealed record MatchStateSnapshot(
    string MatchId,
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<FactionStateSnapshot> Factions
);

public sealed record FactionStateSnapshot(
    string FactionId,
    IReadOnlyList<string> SessionIds,
    int UserCount,
    long Likes,
    long Gifts,
    long Comments
);
