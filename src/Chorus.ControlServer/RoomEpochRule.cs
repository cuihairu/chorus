namespace Chorus.ControlServer;

/// <summary>
/// The room epoch rule (FIX-GM-01, architecture 4.5): a room's ownership epoch is the room's
/// own generation — the count of ownership changes that row has seen, starting at 1 — never a
/// process start, a connection, a mount or a round generation. The GM tool moves it only
/// through this ControlServer admin entry, and the rule here is the contract's arithmetic:
/// a bump defaults to expected + 1, a stated target must be strictly greater than the expected
/// epoch, and the epoch is never lowered, held or overflowed. The authorization that bounds a
/// request to the operator's own tenant/game/room is the FIX-GM-06 line; this rule only states
/// the request shape and the epoch arithmetic, so a caller cannot smuggle an invalid move past
/// a structural check or an overflow.
/// </summary>
public static class RoomEpochRule
{
    /// <summary>The sentinel a proto3 request carries for an omitted target epoch (0 is the
    /// default of an absent int64): "advance by one". The sentinel is unambiguous because a
    /// real bump target is always &gt;= 2 — it must be strictly greater than an epoch that is
    /// itself &gt;= <see cref="EpochFloor"/>.</summary>
    public const long TargetUnspecified = 0;

    /// <summary>The floor of a valid room epoch: the initial ownership row is created at 1, so
    /// no epoch is ever below this. It is also why <see cref="TargetUnspecified"/> is safe.</summary>
    public const long EpochFloor = 1;

    /// <summary>Rejects a request whose scope is incomplete: the tenant, game and room names
    /// are what the control server uses to bound the operation to the caller's own ownership,
    /// so an empty one is an invalid request, not a lookup miss. Pure; the gRPC handler maps the
    /// throw to <c>InvalidArgument</c>.</summary>
    public static void ValidateIdentifiers(string tenantId, string gameId, string roomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId, nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId, nameof(gameId));
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId, nameof(roomId));
    }

    /// <summary>Resolves the target epoch of a bump request. A <paramref name="target"/> of
    /// <see cref="TargetUnspecified"/> (0, the proto3 absent default) means "advance by one";
    /// any other value must be strictly greater than <paramref name="expected"/> — equal, lower,
    /// negative and overflowing moves are all rejected, because the epoch never moves down,
    /// holds still, or wraps. Pure; the gRPC handler maps the throw to <c>InvalidArgument</c>.</summary>
    public static long ResolveTarget(long expected, long target)
    {
        if (expected < EpochFloor)
            throw new ArgumentOutOfRangeException(nameof(expected), expected,
                $"A room epoch is never below {EpochFloor}.");
        if (target == TargetUnspecified)
        {
            if (expected == long.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(expected), expected,
                    "The epoch cannot advance past long.MaxValue.");
            return expected + 1;
        }
        if (target <= expected)
            throw new ArgumentOutOfRangeException(nameof(target), target,
                "A bump target must be strictly greater than the expected epoch.");
        return target;
    }
}
