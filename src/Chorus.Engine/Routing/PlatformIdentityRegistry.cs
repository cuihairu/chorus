namespace Chorus.Engine.Routing;

/// <summary>Chorus-side role a platform account maps to.</summary>
public enum PlatformIdentityKind
{
    /// <summary>主播（房间控制侧，对应审计 StreamerFlow 的 StreamerId）。</summary>
    Streamer = 0,

    /// <summary>玩家/观众（参与侧，对应小游戏阶段 6 的「玩家身份映射」）。</summary>
    Player = 1,
}

/// <summary>One chorus identity: a role plus an opaque chorus-side id.</summary>
/// <param name="Kind">Streamer or Player.</param>
/// <param name="Id">Chorus-side identity id (opaque, e.g. a future streamer account row id).</param>
public sealed record PlatformIdentity(PlatformIdentityKind Kind, string Id);

/// <summary>
/// Platform identity mapping (todo §14 control-plane item, contract = 总体架构审计
/// 核心组件 Identity: LiveCapabilities → Identity): which chorus identity a
/// (Platform, PlatformAccountId) corresponds to. Same discipline as
/// <see cref="PlatformBindingRegistry"/> — mapping conflicts are fail-visible
/// (no implicit overwrite), replace and unbind are compare-and-swap on the
/// current identity so racing callers can never leave two identities active,
/// and Resolve returns null for unknown accounts so callers reject visibly
/// instead of guessing. Today the streamer WebSocket auth is a shared token
/// (StreamerWsFace, no per-platform identity); wiring this registry into that
/// auth lands with the control-plane wiring. In-memory, process-local:
/// persistence lands with §15, like the other routing registries.
/// </summary>
public sealed class PlatformIdentityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Platform, string AccountId), PlatformIdentity> _identities = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _identities.Count;
            }
        }
    }

    /// <summary>
    /// Map a platform account to a chorus identity. Re-mapping the same identity
    /// is idempotent; a conflicting identity fails visibly with the existing one.
    /// </summary>
    public bool TryMap(string platform, string platformAccountId, PlatformIdentity identity, out PlatformIdentity? existing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformAccountId);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Id);

        lock (_gate)
        {
            var key = KeyOf(platform, platformAccountId);
            if (_identities.TryGetValue(key, out var current))
            {
                if (current.Equals(identity))
                {
                    existing = current;
                    return true;
                }

                existing = current;
                return false;
            }

            _identities[key] = identity;
            existing = null;
            return true;
        }
    }

    /// <summary>
    /// Move a mapping to a new identity, guarded by the identity the caller
    /// believes is current; a racing caller fails visibly with the actual mapping.
    /// </summary>
    public bool TryReplace(string platform, string platformAccountId, PlatformIdentity expected, PlatformIdentity replacement, out PlatformIdentity? actual)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformAccountId);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement.Id);

        lock (_gate)
        {
            var key = KeyOf(platform, platformAccountId);
            if (!_identities.TryGetValue(key, out var current))
            {
                actual = null;
                return false;
            }

            if (!current.Equals(expected))
            {
                actual = current;
                return false;
            }

            _identities[key] = replacement;
            actual = null;
            return true;
        }
    }

    /// <summary>
    /// Remove a mapping, guarded by the identity the caller believes is current
    /// so a late unbind cannot delete a newer mapping.
    /// </summary>
    public bool TryUnbind(string platform, string platformAccountId, PlatformIdentity expected, out PlatformIdentity? actual)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformAccountId);
        ArgumentNullException.ThrowIfNull(expected);

        lock (_gate)
        {
            var key = KeyOf(platform, platformAccountId);
            if (!_identities.TryGetValue(key, out var current))
            {
                actual = null;
                return false;
            }

            if (!current.Equals(expected))
            {
                actual = current;
                return false;
            }

            _identities.Remove(key);
            actual = null;
            return true;
        }
    }

    /// <summary>The chorus identity for this platform account, or null — callers reject visibly.</summary>
    public PlatformIdentity? Resolve(string platform, string platformAccountId)
    {
        lock (_gate)
        {
            return _identities.TryGetValue(KeyOf(platform, platformAccountId), out var current) ? current : null;
        }
    }

    /// <summary>A consistent view of all mappings, for diagnostics.</summary>
    public IReadOnlyList<PlatformIdentityMapping> Snapshot()
    {
        lock (_gate)
        {
            return _identities
                .Select(pair => new PlatformIdentityMapping(pair.Key.Platform, pair.Key.AccountId, pair.Value))
                .ToArray();
        }
    }

    private static (string Platform, string AccountId) KeyOf(string platform, string platformAccountId)
        => (platform.ToLowerInvariant(), platformAccountId);
}

/// <summary>One row of the identity table.</summary>
/// <param name="Platform">Platform name as registered (PlatformNames 常量串).</param>
/// <param name="PlatformAccountId">Platform-scoped account id (opaque, case-sensitive).</param>
/// <param name="Identity">The chorus identity currently mapped.</param>
public sealed record PlatformIdentityMapping(string Platform, string PlatformAccountId, PlatformIdentity Identity);
