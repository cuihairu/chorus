namespace Chorus.Redesign.Rpc;

/// <summary>
/// The admission vocabulary every process report speaks (FIX-SCHED-02): whether the
/// control plane may place NEW allocations on the instance. Admission is the scheduler's
/// gate — readiness says the instance works, admission says it wants more rooms. The
/// instance self-reports its capacity states; <c>Draining</c> is the admin's word and the
/// store keeps it against the host's optimism (one way: a drained instance returns to
/// service only by an admin act or replacement, never by reporting harder). <c>Open</c> is
/// the legacy reporter value — tolerated as admitting, so rows written before the
/// vocabulary settled keep working.
/// </summary>
public static class ProcessAdmission
{
    /// <summary>The instance accepts new allocations.</summary>
    public const string Accepting = "Accepting";

    /// <summary>The instance's own budget is exhausted (FIX-PERF-07): it refuses new rooms
    /// until capacity frees, and the scheduler stops offering them.</summary>
    public const string Full = "Full";

    /// <summary>The admin ordered the instance to take no new rooms while its existing
    /// ones finish — admission stops, service does not.</summary>
    public const string Draining = "Draining";

    /// <summary>An instance in this state takes new rooms; anything else — Full, Draining,
    /// an unknown value — does not. Fail closed: an unreadable admission never admits.</summary>
    public static bool AdmitsNewRooms(string admission)
        => admission is Accepting or "Open";
}
