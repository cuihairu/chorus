using System.Collections.Concurrent;

namespace Chorus.Data;

/// <summary>
/// 平台原生榜适配器契约（审计 §9.1 PlatformLeaderboard：调用平台原生排行榜能力）。
/// 实现必须符合平台自身的 API、权限和数据模型（§9.2）；平台未注册/不支持时调用方
/// 必须 fail-closed 降级，不存在默认平台榜。B 站因无官方写回通道（P3 决议、
/// Writeback 恒 false）不提供实现。
/// </summary>
public interface IPlatformLeaderboardAdapter
{
    /// <summary>Platform name constant this adapter serves (OrdinalIgnoreCase).</summary>
    string Platform { get; }

    /// <summary>
    /// Submits one authoritative score to the platform-native board. Returns
    /// false when the platform rejects the write - the caller decides whether
    /// to retry or degrade to the local board only.
    /// </summary>
    bool TrySubmit(string scopeId, string participantId, long score);
}

/// <summary>
/// Registry of platform-native leaderboard adapters. Unregistered platforms
/// fail closed (TrySubmit → false, TryGet → null): wiring an unsupported
/// platform to a leaderboard write must be an explicit decision, never a guess.
/// </summary>
public sealed class PlatformLeaderboardRegistry
{
    private readonly ConcurrentDictionary<string, IPlatformLeaderboardAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) the adapter for its platform; OrdinalIgnoreCase.</summary>
    public void Register(IPlatformLeaderboardAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.Platform))
        {
            throw new ArgumentException("platform 不能为空", nameof(adapter));
        }

        _adapters[adapter.Platform.Trim()] = adapter;
    }

    /// <summary>Returns the adapter, or null when the platform has none (fail-closed).</summary>
    public IPlatformLeaderboardAdapter? TryGet(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return null;
        }

        return _adapters.TryGetValue(platform.Trim(), out var adapter) ? adapter : null;
    }

    /// <summary>True when the platform has a registered adapter.</summary>
    public bool Supports(string? platform) => TryGet(platform) is not null;

    /// <summary>
    /// Registry-level submit: resolves the adapter first and fails visible
    /// (false) for unregistered platforms. Blank scope/participant ids are
    /// contract violations - the adapter (or the pipeline's validate stage)
    /// throws rather than silently dropping.
    /// </summary>
    public bool TrySubmit(string? platform, string scopeId, string participantId, long score)
    {
        var adapter = TryGet(platform);
        if (adapter is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new ArgumentException("scope id 不能为空", nameof(scopeId));
        }

        if (string.IsNullOrWhiteSpace(participantId))
        {
            throw new ArgumentException("participant id 不能为空", nameof(participantId));
        }

        return adapter.TrySubmit(scopeId, participantId, score);
    }

    /// <summary>All registered adapters (observability).</summary>
    public IReadOnlyCollection<IPlatformLeaderboardAdapter> Snapshot() => _adapters.Values.ToArray();
}
