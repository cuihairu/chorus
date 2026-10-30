namespace Chorus.GameServer;

/// <summary>
/// The refusal of a new allocation by the single-host admission budget (FIX-PERF-07).
/// The control plane sees it as a <c>ResourceExhausted</c> gRPC status — the standard
/// "no capacity" signal a scheduler filters an instance on — never as a reply that could
/// be mistaken for a duplicate or a running room.
/// </summary>
public sealed class RoomAdmissionException(string message) : Exception(message);

/// <summary>How one admission attempt came out (FIX-PERF-07).</summary>
internal enum AdmissionOutcome
{
    /// <summary>The room took a slot; the caller must match it with one <see cref="RoomAdmissionBudget.Release"/>.</summary>
    Admitted,

    /// <summary>The active-room cap is reached; the instance refuses until a room leaves.</summary>
    ActiveRoomCap,

    /// <summary>The measured load is at its budget; the instance refuses until it drains.</summary>
    LoadBudget
}

/// <summary>
/// The single GameServer admission budget (FIX-PERF-07): a new allocation is refused when
/// either the active-room count or the measured load is at its budget. The count is an
/// atomic reservation — taken before any data-plane work, so concurrent prepares can never
/// race past the cap — and a failed creation returns its slot, so capacity is never leaked
/// by a room that did not start. Recovery reserves unconditionally: a room this host was
/// assigned must run here, and the scheduler filters capacity elsewhere (FIX-SCHED-02).
/// Invalid configuration fails closed at construction rather than silently disabling a
/// dimension.
/// </summary>
internal sealed class RoomAdmissionBudget
{
    private readonly int? _maxActiveRooms;
    private readonly int? _maxDegradedRooms;
    private int _activeRooms;

    public RoomAdmissionBudget(int? maxActiveRooms, int? maxDegradedRooms)
    {
        if (maxActiveRooms is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxActiveRooms),
                "The active-room admission cap must be a positive integer when set.");
        if (maxDegradedRooms is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDegradedRooms),
                "The measured-load admission budget must be a positive integer when set.");
        _maxActiveRooms = maxActiveRooms;
        _maxDegradedRooms = maxDegradedRooms;
    }

    /// <summary>Rooms holding an admission slot right now (test and report surface).</summary>
    public int ActiveRooms => Volatile.Read(ref _activeRooms);

    /// <summary>
    /// Reserves a slot for a new allocation, or refuses when the measured load (the host's
    /// current degraded-room count) is at its budget or the active-room cap is reached. The
    /// load is checked first and never touches the counter; the count reservation is atomic,
    /// so concurrent admits serialise and the cap holds under a burst.
    /// </summary>
    public AdmissionOutcome TryReserve(int degradedRooms)
    {
        if (_maxDegradedRooms is { } load && degradedRooms >= load)
            return AdmissionOutcome.LoadBudget;
        var now = Interlocked.Increment(ref _activeRooms);
        if (_maxActiveRooms is { } cap && now > cap)
        {
            Interlocked.Decrement(ref _activeRooms);
            return AdmissionOutcome.ActiveRoomCap;
        }
        return AdmissionOutcome.Admitted;
    }

    /// <summary>Would a reserve be refused right now (FIX-SCHED-02)? A pure predicate over
    /// the same two dimensions <see cref="TryReserve"/> enforces, taken without a slot so
    /// the self-report never moves capacity: the answer the process report publishes as
    /// <c>Full</c> — the scheduler filters on it before ever offering a room.</summary>
    public bool WouldRefuse(int degradedRooms)
        => (_maxDegradedRooms is { } load && degradedRooms >= load)
           || (_maxActiveRooms is { } cap && Volatile.Read(ref _activeRooms) >= cap);

    /// <summary>Reserves a slot unconditionally (recovery): a room this host must run is
    /// never refused, but it still counts, so the instance stops accepting new rooms.</summary>
    public void Reserve() => Interlocked.Increment(ref _activeRooms);

    /// <summary>Returns a slot: a room left the host (retire, release, dispose) or its
    /// creation failed before the room took ownership.</summary>
    public void Release() => Interlocked.Decrement(ref _activeRooms);
}
