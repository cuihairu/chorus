namespace Chorus.Engine.Routing;

/// <summary>One row of the current binding table: a platform account's active room.</summary>
/// <param name="Platform">Platform name (PlatformNames 常量串，如 "bilibili"；大小写不敏感归一).</param>
/// <param name="PlatformAccountId">Platform-scoped account id (opaque, case-sensitive).</param>
/// <param name="RoomId">The single room this account is currently bound to.</param>
public sealed record PlatformBinding(string Platform, string PlatformAccountId, string RoomId);

/// <summary>
/// Platform account unique binding store (todo §14 first item, contract =
/// docs/design/overall-architecture.md §4.1): the same
/// (Platform, PlatformAccountId) may map to at most one active Room at any
/// instant. Bind conflicts are fail-visible (no implicit rebind); rebind and
/// unbind are compare-and-swap on the current room so racing callers can never
/// end up with two active bindings, and a stale unbind cannot delete the
/// binding a later rebind created. Resolve returns null for unknown accounts —
/// callers must reject visibly, never guess or broadcast.
/// In-memory, process-local: persistence lands with §15 (持久化层), and
/// production wiring lands with the control-plane routing — until then this
/// registry is the degenerate single-process case, like <see cref="RoomRouteTable"/>.
/// </summary>
public sealed class PlatformBindingRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Platform, string AccountId), PlatformBinding> _bindings = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _bindings.Count;
            }
        }
    }

    /// <summary>
    /// Bind a platform account to a room. Fails visibly (returns false with the
    /// conflicting room id) when the account is already bound elsewhere —
    /// re-binding must go through <see cref="TryRebind"/>.
    /// </summary>
    public bool TryBind(string platform, string platformAccountId, string roomId, out string? conflictRoomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        lock (_gate)
        {
            var key = KeyOf(platform, platformAccountId);
            if (_bindings.TryGetValue(key, out var existing))
            {
                conflictRoomId = existing.RoomId;
                return false;
            }

            _bindings[key] = new PlatformBinding(NormalizePlatform(platform), platformAccountId, roomId);
            conflictRoomId = null;
            return true;
        }
    }

    /// <summary>
    /// Move a binding to a new room, guarded by the room the caller believes is
    /// current. A racing caller holding a stale room id fails visibly with the
    /// actual binding instead of double-binding.
    /// </summary>
    public bool TryRebind(string platform, string platformAccountId, string expectedRoomId, string newRoomId, out string? actualRoomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRoomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRoomId);

        lock (_gate)
        {
            var key = KeyOf(platform, platformAccountId);
            if (!_bindings.TryGetValue(key, out var existing))
            {
                actualRoomId = null;
                return false;
            }

            if (!string.Equals(existing.RoomId, expectedRoomId, StringComparison.Ordinal))
            {
                actualRoomId = existing.RoomId;
                return false;
            }

            _bindings[key] = existing with { RoomId = newRoomId };
            actualRoomId = null;
            return true;
        }
    }

    /// <summary>
    /// Remove a binding, guarded by the room the caller believes is current so a
    /// late unbind from an old room cannot delete a newer binding.
    /// </summary>
    public bool TryUnbind(string platform, string platformAccountId, string expectedRoomId, out string? actualRoomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRoomId);

        lock (_gate)
        {
            var key = KeyOf(platform, platformAccountId);
            if (!_bindings.TryGetValue(key, out var existing))
            {
                actualRoomId = null;
                return false;
            }

            if (!string.Equals(existing.RoomId, expectedRoomId, StringComparison.Ordinal))
            {
                actualRoomId = existing.RoomId;
                return false;
            }

            _bindings.Remove(key);
            actualRoomId = null;
            return true;
        }
    }

    /// <summary>The room this account is currently bound to, or null — callers reject visibly.</summary>
    public string? Resolve(string platform, string platformAccountId)
    {
        lock (_gate)
        {
            return _bindings.TryGetValue(KeyOf(platform, platformAccountId), out var existing) ? existing.RoomId : null;
        }
    }

    /// <summary>A consistent view of the current bindings, for diagnostics.</summary>
    public IReadOnlyList<PlatformBinding> Snapshot()
    {
        lock (_gate)
        {
            return _bindings.Values.ToArray();
        }
    }

    // 平台名走路由/配置，大小写不敏感归一；账号 id 是平台侧不透明串，保持原样。
    private static (string Platform, string AccountId) KeyOf(string platform, string platformAccountId)
        => (NormalizePlatform(platform), platformAccountId);

    private static string NormalizePlatform(string platform)
        => platform.ToLowerInvariant();
}
