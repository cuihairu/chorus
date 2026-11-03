namespace Chorus.ExampleGame;

/// <summary>
/// What a module does once all of a round's control hosts are offline
/// (architecture 8.3, authoritative-sync.md:44-54): the offline policy is a module
/// manifest fact — a declaration, not a host default — pinned with the room's config,
/// validated at admission, and never silently changed by a recovery or config upgrade
/// (FIX-SYNC-04). <see cref="Validate"/> is the admission gate; the <see cref="Canonical"/>
/// the admission persists is the exact string a recovery compares the package's current
/// declaration against, so a policy that changed after the room was admitted refuses the
/// recovery instead of rewriting the live round's offline behavior.
/// </summary>
public enum AllOfflinePolicy
{
    /// <summary>The sentinel for a module that did not choose an all-offline mode; a
    /// policy carrying it is refused at admission (missing behavior).</summary>
    Unset = 0,

    /// <summary>The current baseline: keep computing and accepting in-rule platform events
    /// to the end; never re-queue the offline host's next round (authoritative-sync.md:46).</summary>
    ContinueToEnd = 1,

    /// <summary>After the offline grace, pause at a committed boundary — the logical tick
    /// and game clock freeze in durable state, resumable later.</summary>
    PauseAtBoundary = 2,

    /// <summary>End and compensate per the module's own rule — a durable finish and
    /// platform close barrier, not a room deletion.</summary>
    FinishByRule = 3
}

/// <summary>
/// What the module does with platform inputs that arrive while a round is paused: a bounded
/// durable buffer drained at resume, an out-of-round compensation/rights disposition, or an
/// explicit rejection — never an unbounded queue and never a silent drop of an accepted
/// event (authoritative-sync.md:48-52).
/// </summary>
public enum PausedInputPolicy
{
    /// <summary>The sentinel for a module that did not choose a paused-input handling; a
    /// policy carrying it is refused at admission (missing behavior).</summary>
    Unset = 0,

    /// <summary>Keep the accepted events in a bounded durable buffer, drained at resume in
    /// the original accept order under a per-tick budget.</summary>
    Buffer = 1,

    /// <summary>Dispose of the rights/compensation out of round; the event does not enter a
    /// later resume's play.</summary>
    Compensate = 2,

    /// <summary>Explicitly reject the event; it is acknowledged as refused, not accepted
    /// into the round.</summary>
    Reject = 3
}

/// <summary>
/// The module's declared offline behavior (authoritative-sync.md:44): which all-offline
/// mode applies, how long to wait before pausing, the cap on a pause, how many control
/// participants must be ready to resume, and how paused inputs are handled. The record is
/// the manifest fact — a room carries its policy's <see cref="Canonical"/> in its persisted
/// identity, and a recovery only proceeds when the package's current declaration still
/// matches it (FIX-SYNC-04).
/// </summary>
public sealed record ModuleOfflinePolicy(
    AllOfflinePolicy AllOffline, int OfflineGraceSeconds,
    int MaxPauseSeconds, int ResumeRequiredReadyCount,
    PausedInputPolicy PausedInput)
{
    /// <summary>The shipped production policy: the current baseline — the round keeps
    /// computing and accepting in-rule platform events to the end, and the paused-input
    /// choice is the bounded durable buffer (inert under ContinueToEnd, but declared). The
    /// ready count is one — the smallest room the module admits — so the declaration is
    /// satisfiable for every room it runs, never an unreachable admission refusal.</summary>
    public static ModuleOfflinePolicy ProductionDefault { get; } = new(
        AllOfflinePolicy.ContinueToEnd, 30, 120, 1, PausedInputPolicy.Buffer);

    /// <summary>
    /// The stable persisted form: this exact string rides the room row and the recovery
    /// view, so a later declaration change refuses the recovery instead of silently
    /// rewriting a live round's offline behavior. The format is declaration-exact — a
    /// renamed mode or a reordered duration changes it and fails closed.
    /// </summary>
    public string Canonical =>
        $"{AllOffline}|{OfflineGraceSeconds}|{MaxPauseSeconds}|{ResumeRequiredReadyCount}|{PausedInput}";

    /// <summary>
    /// The admission gate (authoritative-sync.md:44 "illegal count/missing behavior refuses
    /// admission"): an unchosen mode, a ready count the round can never reach (below one, or
    /// above the participant count), or a non-positive duration is a module declaration
    /// error — the room is refused, never admitted under a policy the module did not
    /// actually declare. Returns the violation or null when the policy is valid for
    /// <paramref name="participantCount"/> control participants.
    /// </summary>
    public string? Validate(int participantCount)
    {
        if (AllOffline == AllOfflinePolicy.Unset)
            return "AllOfflinePolicy is not chosen.";
        if (PausedInput == PausedInputPolicy.Unset)
            return "PausedInputPolicy is not chosen.";
        if (ResumeRequiredReadyCount < 1)
            return $"ResumeRequiredReadyCount {ResumeRequiredReadyCount} is below one.";
        if (ResumeRequiredReadyCount > participantCount)
            return $"ResumeRequiredReadyCount {ResumeRequiredReadyCount} exceeds the round's " +
                   $"{participantCount} control participants.";
        if (OfflineGraceSeconds <= 0)
            return $"OfflineGraceSeconds {OfflineGraceSeconds} is not positive.";
        if (MaxPauseSeconds <= 0)
            return $"MaxPauseSeconds {MaxPauseSeconds} is not positive.";
        return null;
    }
}
