namespace Chorus.Adapters.Platforms.MiniGame;

/// <summary>
/// One ranked row handed to a mini-game platform's leaderboard surface.
/// Rank is supplied by the caller (the Chorus side owns ranking determinism);
/// the projector never re-sorts.
/// </summary>
public sealed record MiniGameLeaderboardRow(int Rank, string ParticipantId, long Score);

/// <summary>
/// A platform-neutral leaderboard payload: rows only, no platform envelope. The
/// verified public facts (Douyin) are that an open data domain and ranking APIs
/// exist; the wire shape inside that sandbox is not publicly documented, so it
/// is produced here as rows and formatted by the out-of-repo platform-side
/// adapter (待外部).
/// </summary>
public sealed record MiniGameLeaderboardProjection(
    string Platform,
    MiniGameCapability TargetCapability,
    IReadOnlyList<MiniGameLeaderboardRow> Rows);

/// <summary>
/// Projects Chorus leaderboard rows into a mini-game platform leaderboard
/// payload (todo §17 排行榜适配). Capability-gated: projecting for a platform
/// whose ranking capability was not confirmed in public official documentation
/// returns null (fail visible) instead of assuming the capability exists - 禁止
/// 编造平台能力. The projection is the engine-side half only; drawing inside the
/// platform sandbox is platform JavaScript outside this repository (待外部).
/// </summary>
public sealed class MiniGameLeaderboardProjector
{
    private readonly MiniGamePlatformCapabilities _capabilities;

    public MiniGameLeaderboardProjector(MiniGamePlatformCapabilities capabilities)
        => _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));

    /// <summary>
    /// Project up to <paramref name="top"/> rows for the given platform. Null
    /// when the platform is unknown or does not publicly document a ranking
    /// capability. Rows are emitted in the order given (caller is authoritative
    /// for rank); rows with a blank participant id throw - a blank participant
    /// is a caller bug, not a filterable condition.
    /// </summary>
    public MiniGameLeaderboardProjection? Project(
        string platform,
        IReadOnlyList<MiniGameLeaderboardRow> sourceRows,
        int top = 20,
        MiniGameCapability targetCapability = MiniGameCapability.FriendRankData)
    {
        ArgumentNullException.ThrowIfNull(sourceRows);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(top, 0);

        var capabilities = _capabilities.TryGet(platform);
        if (capabilities is null || !capabilities.Supports(targetCapability))
        {
            return null; // 未知平台 / 未找到公开资料：不假设平台能力。
        }

        var rows = new List<MiniGameLeaderboardRow>(Math.Min(top, sourceRows.Count));
        foreach (var row in sourceRows.Take(top))
        {
            if (string.IsNullOrWhiteSpace(row.ParticipantId))
            {
                throw new ArgumentException("participant id 不能为空", nameof(sourceRows));
            }

            rows.Add(row with { ParticipantId = row.ParticipantId.Trim() });
        }

        return new MiniGameLeaderboardProjection(capabilities.Platform, targetCapability, rows);
    }
}
