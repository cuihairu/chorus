namespace Chorus.ControlServer;

/// <summary>An authenticated human GM operator, separate from any process identity. The
/// identity comes from the operator credential (a bearer token), never from the request body
/// or the process certificate.</summary>
public sealed record OperatorIdentity(
    string OperatorId, string Role, IReadOnlyList<string> Tenants, IReadOnlyList<string> Games);

/// <summary>
/// The role and scope half of operator authorization (FIX-GM-06, architecture 12.1). A role
/// ranks the commands an operator may run; the tenants/games arrays scope the target an
/// operator may act on, with an empty array meaning "all". The rules are pure: a command
/// declares the minimum role it needs and the target it touches, and the gate rejects a
/// caller whose role is too low or whose scope does not cover the target.
/// </summary>
public static class OperatorAuthorizer
{
    /// <summary>Ordered low to high. A command declares the minimum rank it needs; an operator
    /// is authorized when their role ranks at or above the command's requirement.</summary>
    public static readonly string[] RoleRank = ["viewer", "operator", "gm", "super"];

    public static bool HasRole(string role, string requiredRole)
    {
        var actual = Array.IndexOf(RoleRank, role);
        var needed = Array.IndexOf(RoleRank, requiredRole);
        return actual >= 0 && needed >= 0 && actual >= needed;
    }

    /// <summary>True when the operator may act on the target tenant/game: an empty scope array
    /// covers every tenant/game, otherwise the target must be listed. An empty target scope
    /// means the action carries no tenant/game constraint (a cluster-wide or partition-wide
    /// command) — only the non-empty dimensions are checked, so a scope-limited operator is
    /// not refused a global command it is otherwise role-authorized to run.</summary>
    public static bool InScope(OperatorIdentity operatorIdentity, string tenantId, string gameId)
    {
        if (tenantId.Length > 0 && operatorIdentity.Tenants.Count > 0 &&
            !operatorIdentity.Tenants.Contains(tenantId, StringComparer.Ordinal))
            return false;
        if (gameId.Length > 0 && operatorIdentity.Games.Count > 0 &&
            !operatorIdentity.Games.Contains(gameId, StringComparer.Ordinal))
            return false;
        return true;
    }
}
