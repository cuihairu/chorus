using System;
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

        /// <summary>Closes the wire. A closed transport answers nothing further.</summary>
        Task CloseAsync();
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
