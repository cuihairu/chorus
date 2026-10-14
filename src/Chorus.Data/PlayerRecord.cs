using System.Collections.Concurrent;

namespace Chorus.Data;

/// <summary>
/// 玩家长期资料（审计 §8.1「玩家资料与进度」）。The profile is a key-value bag:
/// commands update specific keys, so a partial write never wipes fields owned by
/// another command type. <c>Version</c> starts at 1 and advances by one per
/// applied update - it is the optimistic-concurrency guard and the idempotency
/// anchor at once.
/// </summary>
public sealed record PlayerRecord
{
    public PlayerRecord(string playerId, long version, IReadOnlyDictionary<string, string> profile)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            throw new ArgumentException("player id 不能为空", nameof(PlayerId));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Version), version, "玩家资料版本从 1 开始");
        }

        PlayerId = playerId;
        Version = version;
        Profile = profile ?? throw new ArgumentNullException(nameof(Profile));
    }

    /// <summary>Player-scoped identity; cross-platform uniqueness is the control plane's job.</summary>
    public string PlayerId { get; init; }

    /// <summary>Monotonic profile version; 1 on create, +1 per applied update.</summary>
    public long Version { get; init; }

    /// <summary>Profile keys; empty means a no-op payload.</summary>
    public IReadOnlyDictionary<string, string> Profile { get; init; }
}

/// <summary>
/// In-memory player profile store with optimistic concurrency (audit §8.1:
/// 「必须定义版本、并发写入、幂等和查询一致性」).
///
/// <list type="bullet">
/// <item>版本守卫：更新只接受 current + 1；旧值回写与跳版本都被拒（fail-visible，
/// last-writer-wins 被挡住，状态从不被旧值覆盖）。</item>
/// <item>幂等：同版本且键值内容一致的重放是 no-op 成功；同版本但内容不同是冲突。</item>
/// <item>查询一致性：读取总返回已提交的最新版本；快照按 PlayerId 有序。</item>
/// </list>
/// 生产接线（Data Worker 落库）属后续项；此处是仓储语义的权威实现。
/// </summary>
public sealed class PlayerRepository
{
    private readonly ConcurrentDictionary<string, PlayerRecord> _players = new(StringComparer.Ordinal);

    /// <summary>Number of stored players.</summary>
    public int Count => _players.Count;

    /// <summary>Returns the latest committed record, or null for an unknown player.</summary>
    public PlayerRecord? TryGet(string? playerId) =>
        !string.IsNullOrWhiteSpace(playerId) && _players.TryGetValue(playerId, out var record) ? record : null;

    /// <summary>
    /// Creates a player at version 1. Same-content replay is an idempotent
    /// no-op (true); a different payload for the same id is a conflict (false);
    /// creating at any version other than 1 is a caller contract error.
    /// </summary>
    public bool TryCreate(PlayerRecord record)
    {
        if (record.Version != 1)
        {
            throw new ArgumentException("创建只能落在 version 1", nameof(record));
        }

        if (_players.TryGetValue(record.PlayerId, out var existing))
        {
            return SameContent(existing, record);
        }

        return _players.TryAdd(record.PlayerId, record);
    }

    /// <summary>
    /// Applies the next profile version (merge: keys in the incoming payload are
    /// written, all other keys survive). Same version = idempotent replay check;
    /// any other version than current or current+1 is a conflict (false) and
    /// leaves the stored record untouched.
    /// </summary>
    public bool TryUpdate(PlayerRecord record)
    {
        if (!_players.TryGetValue(record.PlayerId, out var current))
        {
            // Unknown player: updates never auto-create (fail-visible).
            return false;
        }

        if (record.Version == current.Version)
        {
            return SameContent(current, record);
        }

        if (record.Version != current.Version + 1)
        {
            // Stale write or version gap: reject, never overwrite with an older value.
            return false;
        }

        var merged = new Dictionary<string, string>(current.Profile, StringComparer.Ordinal);
        foreach (var (key, value) in record.Profile)
        {
            merged[key] = value;
        }

        return _players.TryUpdate(record.PlayerId, record with { Profile = merged }, current);
    }

    /// <summary>All stored players ordered by PlayerId (observability/snapshot).</summary>
    public IReadOnlyList<PlayerRecord> Snapshot() =>
        _players.Values.OrderBy(p => p.PlayerId, StringComparer.Ordinal).ToArray();

    private static bool SameContent(PlayerRecord stored, PlayerRecord incoming)
    {
        foreach (var (key, value) in incoming.Profile)
        {
            if (!stored.Profile.TryGetValue(key, out var current) || current != value)
            {
                return false;
            }
        }

        return true;
    }
}
