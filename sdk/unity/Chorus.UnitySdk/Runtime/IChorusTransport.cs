using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The transport seam (FIX-U3D-07): the login link's wire is behind this interface,
    /// so the SDK's orchestration (mount → login → gameplay start → session) stays
    /// platform-neutral and fully testable while the concrete wire — the Chorus edge's
    /// WSS JSON protocol — lives in <see cref="EdgeChorusTransport"/>.
    ///
    /// Like <see cref="ClientIntent"/>, the login intent deliberately omits the facts
    /// only a transport can know: the tenant (the edge names it per connection from its
    /// own application map) and the gateway/connection identities (the edge stamps
    /// them). The SDK owns the session-security facts: the login attempt id and the
    /// next resume secret, freshly generated per login.
    /// </summary>
    public interface IChorusTransport
    {
        /// <summary>Opens the wire and performs its opening handshake.</summary>
        Task ConnectAsync(CancellationToken cancellationToken);

        /// <summary>Exchanges the platform mount for the session facts.</summary>
        Task<LoginAnswer> LoginAsync(LoginIntent intent, CancellationToken cancellationToken);

        /// <summary>Starts the gameplay binding the login's session attaches to, and
        /// answers the binding id.</summary>
        Task<string> StartGameplayAsync(string accessCredential, string gameId, string operationId,
            CancellationToken cancellationToken);

        /// <summary>Enqueues the mount's ticket into one match partition and answers
        /// the ticket facts the queue acknowledged.</summary>
        Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken);

        /// <summary>Withdraws the ticket and answers its terminal state.</summary>
        Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
            CancellationToken cancellationToken);

        /// <summary>Answers the atomic room entry: the match's committed room identity
        /// and the seat's room facts — the first frame the room produces rides behind
        /// the entry, consumed by the subscription link that owns it.</summary>
        Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken);

        /// <summary>Opens the room's sync stream (FIX-U3D-09): states the subscriber's
        /// applied position on the wire and answers the pull-style stream that owns the
        /// downlink from here on. A newer subscribe supersedes the stream it returned
        /// before; the superseded stream refuses to advance.</summary>
        Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken);

        /// <summary>Sends one game intent into the room's inbox and answers the
        /// accepted word (FIX-U3D-10). The answer rides the stream's pumping: the
        /// stream owns the downlink and resolves the waiting send when the receipt
        /// arrives.</summary>
        Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken);

        /// <summary>Rotates onto a fresh session (FIX-U3D-11): presents the old session's
        /// resume secret, answers the resumed session's facts — the same shape login
        /// answered — and leaves the room's re-entry (roomJoined, then the head snapshot
        /// the declared baseline continues from) to <see cref="AwaitRejoinedRoomAsync"/>.</summary>
        Task<LoginAnswer> ResumeAsync(ResumeIntent intent, CancellationToken cancellationToken);

        /// <summary>Answers the room the resume re-entered: the server's participation
        /// record names it, the client's last-seen room was only diagnostics. A resume
        /// with nowhere to return announces nothing — the caller bounds this wait. A
        /// seat whose round stands parked gets the park notice as this call's refusal:
        /// EdgeProtocolException coded roomSuspended, carrying the recovery's epoch.</summary>
        Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken);

        /// <summary>Withdraws the seat from one round (FIX-U3D-12). The wire
        /// acknowledges no leave; a refusal names the operation id on the stream.</summary>
        Task LeaveRoundAsync(string roomId, string operationId, CancellationToken cancellationToken);

        /// <summary>Ends the session: the logout revokes durably before the gateway
        /// closes, and the SDK closes its own side after the send (no ack exists).</summary>
        Task LogoutAsync(string operationId, CancellationToken cancellationToken);

        /// <summary>Asks one leaderboard read and answers the wire's reply (FIX-U3D-13).
        /// The answer rides the downlink whoever pumps it — a live stream's loop resolves
        /// it the way it resolves command receipts, and an unsubscribed transport pumps
        /// its own short loop until the answer lands. Exactly one reply answers each
        /// operation id.</summary>
        Task<LeaderboardAnswer> LeaderboardQueryAsync(LeaderboardQueryIntent intent,
            CancellationToken cancellationToken);

        /// <summary>Closes the wire. A closed transport answers nothing further.</summary>
        Task CloseAsync();
    }

    /// <summary>One leaderboard read the session asks the transport to send (FIX-U3D-13):
    /// the board's read key and which of the three reads it wants. The tenant and the
    /// game never ride the client — the edge names them from the connection's session
    /// facts.</summary>
    public sealed class LeaderboardQueryIntent
    {
        /// <summary>The operation id the SDK generated for this read (diagnostics and the
        /// answer's address).</summary>
        public string OperationId { get; set; } = string.Empty;

        /// <summary>Which read: "top", "rank" or "around".</summary>
        public string Kind { get; set; } = string.Empty;

        public string BoardId { get; set; } = string.Empty;

        /// <summary>Empty = the all-time bucket.</summary>
        public string? PeriodId { get; set; }

        /// <summary>Empty = the global scope.</summary>
        public string? ScopeId { get; set; }

        /// <summary>The RANK-01 subject type: 0 player, 1 streamer, 2 faction.</summary>
        public int Subject { get; set; }

        /// <summary>The subject a rank or around read names; empty reads the connection's
        /// own identity.</summary>
        public string? SubjectId { get; set; }

        /// <summary>Top only: the page size (1..1000).</summary>
        public int MaxEntries { get; set; }

        /// <summary>Around only: the neighbour window (1..50).</summary>
        public int Window { get; set; }

        /// <summary>Top only: the previous page's continuation cursor.</summary>
        public string? Cursor { get; set; }
    }

    /// <summary>The wire's answer to one leaderboard read: the entries at one read view.
    /// Ranks ride the entries only for an around read; a top page's order is the board's
    /// order. <see cref="LeaderboardAnswer.Stale"/> true ends a walk — the session turns
    /// it into <see cref="LeaderboardStaleException"/>.</summary>
    public sealed class LeaderboardAnswer
    {
        public bool Found { get; set; }
        public bool Stale { get; set; }
        public IReadOnlyList<LeaderboardRankedWireEntry> Entries { get; set; }
            = Array.Empty<LeaderboardRankedWireEntry>();
        public string ReadVersion { get; set; } = string.Empty;
        public string? NextCursor { get; set; }
        public bool HasMore { get; set; }
    }

    public sealed class LeaderboardRankedWireEntry
    {
        public long Rank { get; set; }
        public string SubjectId { get; set; } = string.Empty;
        public long Score { get; set; }
    }

    /// <summary>What one resume hands the transport: the session it resumes, the secret
    /// that session was registered with, and the fresh secret the resumed session
    /// rotates onto. The room facts ride as diagnostics only — the server's
    /// participation record decides where the resume lands (contracts 3.1).</summary>
    public sealed class ResumeIntent
    {
        /// <summary>The operation id the SDK generated for this resume (diagnostics).</summary>
        public string ResumeOperationId { get; set; } = string.Empty;

        /// <summary>The session being resumed.</summary>
        public string PreviousSessionId { get; set; } = string.Empty;

        /// <summary>The 32-byte secret the previous session was registered with.</summary>
        public byte[] ResumeCredential { get; set; } = Array.Empty<byte>();

        /// <summary>The 32-byte secret the client generated for the resumed session.</summary>
        public byte[] NextResumeSecret { get; set; } = Array.Empty<byte>();

        /// <summary>The room the game last saw (diagnostics; the wire logs it, the
        /// re-entry never trusts it).</summary>
        public string? LastSeenRoomId { get; set; }

        /// <summary>The state version the game's applier last applied — seeds the
        /// re-baseline the re-entry's snapshot continues from.</summary>
        public long LastAppliedStateVersion { get; set; }
    }

    /// <summary>One queue intent: which partition and what the match carries.</summary>
    public sealed class JoinMatchIntent
    {
        /// <summary>The operation id the SDK generated for this join (diagnostics).</summary>
        public string OperationId { get; set; } = string.Empty;

        /// <summary>The partition (for example "standard"); the server's partition rules
        /// decide who meets.</summary>
        public string QueueType { get; set; } = string.Empty;

        /// <summary>Opaque options the SDK never reads — the partition composes what it
        /// can name.</summary>
        public string Options { get; set; } = string.Empty;
    }

    /// <summary>One ticket's facts as the edge answered them: its identity, where it
    /// stands, and — once committed — the room that names it.</summary>
    public sealed class MatchTicket
    {
        /// <summary>The ticket's identity.</summary>
        public string TicketId { get; set; } = string.Empty;

        /// <summary>Where the ticket stands: "Queued", "Matched" or "Cancelled".</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>The committed room, when the state is "Matched".</summary>
        public string RoomId { get; set; } = string.Empty;

        /// <summary>The reservation the commit consumed, when matched.</summary>
        public string ReservationId { get; set; } = string.Empty;
    }

    /// <summary>The atomic room entry: the room the match committed and the seat's
    /// room facts — the entry answers only when both the commit and the seat's room
    /// joined facts are in hand, so the game never sees a match without a room.</summary>
    public sealed class JoinedRoom
    {
        /// <summary>The room the match committed.</summary>
        public string RoomId { get; set; } = string.Empty;

        /// <summary>The room's epoch at entry.</summary>
        public long RoomEpoch { get; set; }

        /// <summary>Which side this seat plays, as the room's vocabulary names it.</summary>
        public string Participation { get; set; } = string.Empty;

        /// <summary>The module identity the room's frames carry.</summary>
        public string ModuleVersion { get; set; } = string.Empty;

        /// <summary>The rules the room runs, as the module names it.</summary>
        public string ConfigHash { get; set; } = string.Empty;

        /// <summary>The ticket this entry consumed.</summary>
        public string TicketId { get; set; } = string.Empty;

        /// <summary>The reservation the commit consumed.</summary>
        public string ReservationId { get; set; } = string.Empty;
    }

    /// <summary>What one login hands the transport: the platform mount's facts plus the
    /// session-security facts the SDK generated. Never carries tenant or gateway
    /// identities — the transport adds those on the wire.</summary>
    public sealed class LoginIntent
    {
        /// <summary>The platform the mount answered for.</summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>The platform application id from the mount.</summary>
        public string AppId { get; set; } = string.Empty;

        /// <summary>The mount's session context — the platform's own launch credential,
        /// verified server-side before anything else happens.</summary>
        public string LaunchCredential { get; set; } = string.Empty;

        /// <summary>The attempt id the SDK generated for this login (diagnostics and
        /// duplicate-mount bookkeeping on the server).</summary>
        public string LoginAttemptId { get; set; } = string.Empty;

        /// <summary>The 32-byte secret the client generated and keeps for the resume
        /// rotation — the server commits it with the grant.</summary>
        public byte[] NextResumeSecret { get; set; } = Array.Empty<byte>();
    }
}
