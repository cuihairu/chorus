namespace Chorus.GameServer;

/// <summary>
/// The kind of a room connection for the all-offline judgment
/// (authoritative-sync.md:42): a <see cref="Control"/> link is one of the round's
/// control participants and counts toward the judgment; an <see cref="Observer"/>
/// link never does — a viewer disconnecting, or zero observers, can never make the
/// round look all-offline (FIX-SYNC-05).
/// </summary>
public enum PresenceLinkKind
{
    /// <summary>A round participant whose session drives the frame; counts toward the
    /// all-control-sessions-offline judgment.</summary>
    Control = 0,

    /// <summary>A viewer/spectator link; recorded but never judged.</summary>
    Observer = 1
}

/// <summary>
/// A connection fact for a room link (authoritative-sync.md:42): disconnect, broadcast
/// close, active exit, ban and service failure are the five distinct reason inputs a
/// control session can end with, and <see cref="TimedOut"/> is the server-side heartbeat
/// timeout judgment — the tracker synthesizes it; callers never report it.
/// </summary>
public enum ControlLinkReason
{
    /// <summary>The connection is established (or a keepalive confirms it live).</summary>
    Connected = 0,

    /// <summary>断线: the connection dropped without a controlled close.</summary>
    Disconnected = 1,

    /// <summary>关播: the host closed the broadcast/stream deliberately.</summary>
    BroadcastClosed = 2,

    /// <summary>主动退出: the participant exited the round of its own accord.</summary>
    Exited = 3,

    /// <summary>封禁: the platform banned the participant.</summary>
    Banned = 4,

    /// <summary>服务故障: the serving process failed.</summary>
    ServiceFailure = 5,

    /// <summary>心跳超时: the server judged a live connection dead because its heartbeat
    /// exceeded the server timeout. Reports never carry this value — <see cref="ControlPresenceTracker.Sample"/>
    /// derives it.</summary>
    TimedOut = 6
}

/// <summary>
/// The all-offline judgment at a sample instant (FIX-SYNC-05): <see cref="Mixed"/> while
/// at least one control session is live; <see cref="AllOfflineWithinGrace"/> once every
/// control session is offline but the continuous all-offline duration is still inside the
/// round's declared grace; <see cref="AllOfflineGraceElapsed"/> once the grace has elapsed
/// and the room may pause (SYNC-06). A reconnect that breaks the all-offline run resets the
/// window — the grace never carries across an interruption.
/// </summary>
public enum ControlPresenceVerdict
{
    Mixed = 0,
    AllOfflineWithinGrace = 1,
    AllOfflineGraceElapsed = 2
}

/// <summary>A control participant's judged presence at a sample.</summary>
public readonly record struct ControlParticipantPresence(
    string ParticipantId, bool Online, ControlLinkReason? Reason,
    long Generation, DateTimeOffset LastSeen);

/// <summary>
/// The full judgment: the control population, who is online/offline, the verdict and —
/// when all offline — how long the continuous all-offline run has lasted.
/// </summary>
public readonly record struct ControlPresenceSample(
    int ControlParticipants, int Online, int Offline,
    ControlPresenceVerdict Verdict, TimeSpan AllOfflineFor,
    IReadOnlyList<ControlParticipantPresence> Participants);

/// <summary>
/// FIX-SYNC-05: the server-side judge of "all control sessions offline"
/// (authoritative-sync.md:42) — offline is a server fact built from the round's control
/// participants, each at its current connection generation, the server heartbeat timeout
/// and the round's declared grace; it is never a client opinion and never a broadcast of
/// a single connection's fate. A control session is online only when its current-generation
/// connection is live (heartbeat inside the server timeout) and has not ended; it is offline
/// when its current-generation connection ended (disconnect/broadcast close/active exit/ban/
/// service failure) or when the server timed it out. An observer link, a zero-observer room,
/// an old-generation close or an old-generation reconnect never changes the judgment, and a
/// single live control session keeps the room Mixed — the grace protects a startup or
/// reconnect window, and any reconnect that restores a control session resets the all-offline
/// run so the grace restarts, never carries over.
/// </summary>
/// <remarks>Single-consumer and free of locks, mirroring <c>BaselineApplier</c>: the room's
/// actor feeds connection facts in tick order and samples once per tick. <paramref name="now"/>
/// on every call must be non-decreasing (the room passes monotonic tick time); a backwards
/// clock would corrupt the offline window.</remarks>
public sealed class ControlPresenceTracker
{
    private readonly TimeSpan _serverTimeout;
    private readonly TimeSpan _offlineGrace;
    private readonly Dictionary<string, ParticipantState> _control = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observers = new(StringComparer.Ordinal);
    private DateTimeOffset _seededAt = DateTimeOffset.MinValue;

    public ControlPresenceTracker(TimeSpan serverTimeout, TimeSpan offlineGrace)
    {
        if (serverTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(serverTimeout),
                "The server timeout must be positive.");
        if (offlineGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(offlineGrace),
                "The offline grace must be positive.");
        _serverTimeout = serverTimeout;
        _offlineGrace = offlineGrace;
    }

    /// <summary>Declares the round's control participants (the room's admission set). A
    /// participant not yet carrying a live connection is offline — no current valid control
    /// session — with its offline window starting at the seed instant.</summary>
    public void SeedControlParticipants(IEnumerable<string> participantIds, DateTimeOffset now)
    {
        foreach (var id in participantIds)
        {
            if (id.Length == 0) throw new ArgumentException("Empty participant id.", nameof(participantIds));
            if (!_control.ContainsKey(id))
                _control[id] = new ParticipantState { OfflineSince = now };
        }
        if (_seededAt == DateTimeOffset.MinValue) _seededAt = now;
    }

    /// <summary>A connection established at <paramref name="generation"/> for
    /// <paramref name="identity"/>. A control connection at a generation newer than the
    /// participant's current one supersedes it and puts the participant online; an
    /// old-generation connect is a stale fact and changes nothing.</summary>
    public void Connect(long generation, string identity, PresenceLinkKind kind, DateTimeOffset now)
    {
        if (kind == PresenceLinkKind.Observer)
        {
            _observers.Add(identity);
            return;
        }
        if (!_control.TryGetValue(identity, out var state)) return; // not a control participant
        if (generation < state.Generation) return;                  // stale connection fact
        state.Generation = generation;
        state.Online = true;
        state.Reason = null;
        state.LastSeen = now;
        state.OfflineSince = null;
    }

    /// <summary>Refreshes a live current-generation control connection; a stale generation
    /// or an offline participant is left untouched.</summary>
    public void Heartbeat(long generation, string identity, DateTimeOffset now)
    {
        if (!_control.TryGetValue(identity, out var state)) return;
        if (generation != state.Generation || !state.Online) return;
        state.LastSeen = now;
    }

    /// <summary>A control session ended at <paramref name="generation"/> for one of the five
    /// terminal reasons. A generation older than the participant's current one is a late
    /// close from a superseded connection and is ignored — it cannot take the current
    /// connection offline; an unknown or newer generation is likewise ignored.</summary>
    public void Disconnect(long generation, string identity, ControlLinkReason reason, DateTimeOffset now)
    {
        if (reason is ControlLinkReason.Connected or ControlLinkReason.TimedOut)
            throw new ArgumentException($"Reason {reason} is not a terminal report.", nameof(reason));
        if (!_control.TryGetValue(identity, out var state)) return;
        if (generation != state.Generation) return;
        state.Online = false;
        state.Reason = reason;
        state.OfflineSince = now;
    }

    /// <summary>
    /// The judgment at <paramref name="now"/>: the online/offline split over the control
    /// participants and the verdict. A control participant whose current-generation
    /// connection's heartbeat is older than the server timeout is judged offline with
    /// <see cref="ControlLinkReason.TimedOut"/> (the timeout crossing instant is its
    /// offline-window start). All offline is only reached when every control participant is
    /// offline; the all-offline run is measured from the latest offline-window start among
    /// them, so a reconnect that restores any control session resets the run.
    /// </summary>
    public ControlPresenceSample Sample(DateTimeOffset now)
    {
        var participants = new List<ControlParticipantPresence>(_control.Count);
        var windowStart = DateTimeOffset.MinValue;
        var offline = 0;
        foreach (var (id, state) in _control)
        {
            if (state.Online && now - state.LastSeen >= _serverTimeout)
            {
                state.Online = false;
                state.Reason = ControlLinkReason.TimedOut;
                state.OfflineSince = state.LastSeen + _serverTimeout;
            }
            if (state.Online)
            {
                participants.Add(new(id, true, null, state.Generation, state.LastSeen));
            }
            else
            {
                offline++;
                var since = state.OfflineSince ?? _seededAt;
                if (since > windowStart) windowStart = since;
                participants.Add(new(id, false, state.Reason, state.Generation, state.LastSeen));
            }
        }
        var allOffline = _control.Count > 0 && offline == _control.Count;
        var forHowLong = allOffline ? now - windowStart : TimeSpan.Zero;
        var verdict = !allOffline ? ControlPresenceVerdict.Mixed
            : forHowLong >= _offlineGrace ? ControlPresenceVerdict.AllOfflineGraceElapsed
            : ControlPresenceVerdict.AllOfflineWithinGrace;
        participants.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.ParticipantId, b.ParticipantId));
        return new ControlPresenceSample(_control.Count, _control.Count - offline, offline,
            verdict, forHowLong, participants);
    }

    private sealed class ParticipantState
    {
        public long Generation;
        public bool Online;
        public ControlLinkReason? Reason;
        public DateTimeOffset LastSeen;
        public DateTimeOffset? OfflineSince;
    }
}
