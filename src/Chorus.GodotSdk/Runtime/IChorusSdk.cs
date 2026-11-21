using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.GodotSdk
{
    /// <summary>
    /// The unified Godot SDK entry (EK-1): the one surface game code programs against.
    /// The platform specifics live behind <see cref="IPlatformAdapter"/>; the transport
    /// (login, subscribe, intents) is the <see cref="IChorusTransport"/> seam. This
    /// contract fixes the shape the transport implements, so the game code the SDK
    /// serves is written once — the same shape the Unity SDK serves.
    /// </summary>
    public interface IChorusSdk
    {
        /// <summary>
        /// Mounts through the platform adapter, exchanges the mount for a Chorus session
        /// (login, then gameplay start) and answers the session. The adapter's mount
        /// context is consumed exactly once; every later call rides the session's own
        /// access credential. The <see cref="SessionEstablished"/> event fires with the
        /// new session before this call returns.
        /// </summary>
        Task<ChorusSession> ConnectAsync(SdkConfiguration configuration, IPlatformAdapter adapter,
            CancellationToken cancellationToken);

        /// <summary>
        /// Rotates a dropped session onto a fresh one: presents the session's resume
        /// secret, answers a new session carrying a new secret to persist, and — when the
        /// server's participation record still holds a round — the room's re-entry rides
        /// <see cref="ChorusSession.AwaitRejoinedRoomAsync"/>, whose snapshot rebaselines
        /// the game's applier. No platform adapter takes part. The
        /// <see cref="SessionEstablished"/> event fires with the new session before this
        /// call returns.
        /// </summary>
        Task<ChorusSession> ResumeAsync(SdkConfiguration configuration, string previousSessionId,
            byte[] resumeCredential, string? lastSeenRoomId, long lastAppliedStateVersion,
            CancellationToken cancellationToken);
    }

    /// <summary>One mounted, logged-in session. The session carries the login's answer and
    /// the gameplay binding, subscribes to rooms and sends intents. Mirrors the Unity
    /// SDK's <c>ChorusSession</c>, and carries the event surface (EK-1): the facts that
    /// land after the session exists — match, room, disconnect — ride events a Godot
    /// <c>Node</c> bridge re-emits as signals.</summary>
    public interface ChorusSession
    {
        /// <summary>Fires when the queue committed the match: the ticket is Matched.</summary>
        event Action<MatchTicket>? MatchJoined;

        /// <summary>Fires when the ticket was withdrawn and the queue answered it.</summary>
        event Action<MatchTicket>? MatchCancelled;

        /// <summary>Fires when the seat's room entry answers — the committed room plus
        /// the seat's room facts together. On a resume this is the re-entry.</summary>
        event Action<JoinedRoom>? RoomJoined;

        /// <summary>Fires when the seat leaves a room; the argument is the room id.</summary>
        event Action<string>? RoomLeft;

        /// <summary>Fires when the wire closes — a disconnect or a logout. A disconnect
        /// leaves the session resumable; a logout does not.</summary>
        event Action? Disconnected;

        /// <summary>The login's answer: the Chorus identities the mount was exchanged for.</summary>
        LoginAnswer Login { get; }

        /// <summary>The gameplay binding the session started with (the mount's game attach).
        /// Null on a resumed session: the binding lives on the server's mount record, which
        /// the resume's re-entry restores — the client never starts it again.</summary>
        string? BindingId { get; }

        /// <summary>The 32-byte secret that resumes this session: persist it next to
        /// <see cref="Login"/>'s session id. The next resume presents it and answers a
        /// session whose own secret replaces it — the rotation never reuses a secret.</summary>
        byte[] ResumeCredential { get; }

        /// <summary>Subscribes to one room's authoritative stream and answers a pull-style
        /// stream the game drives from its own loop. The applied baseline rides the
        /// request: a baseline at the head continues with deltas alone, anything older
        /// answers with a full snapshot first — the same call serves first mount, resume
        /// and catch-up, exactly like the server's subscribe contract.</summary>
        Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken);

        /// <summary>Sends one client intent into the room inbox; the reply carries the
        /// sequence (or the duplicate flag for a retried command id).</summary>
        Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken);

        /// <summary>Enqueues the mount's ticket into one match partition and answers the
        /// ticket the queue acknowledged — the match itself rides <see cref="AwaitRoomAsync"/>.</summary>
        Task<MatchTicket> JoinMatchAsync(string queueType, string? options,
            CancellationToken cancellationToken);

        /// <summary>Withdraws the ticket and answers its terminal state.</summary>
        Task<MatchTicket> CancelMatchAsync(string ticketId, CancellationToken cancellationToken);

        /// <summary>Answers the atomic room entry: the committed room and the seat's room
        /// facts together — the match's room and the seat in it, never one without the other.</summary>
        Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken);

        /// <summary>Answers the room a resume re-entered: the server's participation record
        /// names it, and the head snapshot behind the entry is the re-baseline the game's
        /// applier continues from. A resume with nowhere to return announces nothing on
        /// the wire, so the game bounds this wait itself.</summary>
        Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken);

        /// <summary>Re-declares the gameplay binding on the open wire: the startGameplay the
        /// login already ran, sent again. It is the resume-ready declaration.</summary>
        Task<string> RedeclareGameplayAsync(string gameId, CancellationToken cancellationToken);

        /// <summary>Withdraws the seat from one round — the normal exit from play. The wire
        /// acknowledges no leave: the call completes when the frame is sent.</summary>
        Task LeaveRoundAsync(string roomId, CancellationToken cancellationToken);

        /// <summary>Ends the session for good — the normal exit from the service: the logout
        /// revokes the session durably before the gateway closes, and a later resume with
        /// the held secret is refused as revoked. A mere <see cref="DisconnectAsync"/> keeps
        /// the session resumable — this one does not.</summary>
        Task LogoutAsync(CancellationToken cancellationToken);

        /// <summary>Reads one page of a board's TopN (the board's declared order at one read
        /// view, bounded and paginated). A continuation whose board changed or whose
        /// snapshot expired throws <see cref="LeaderboardStaleException"/>.</summary>
        Task<LeaderboardTop> GetTopAsync(LeaderboardKey key, int maxEntries, string? cursor,
            CancellationToken cancellationToken);

        /// <summary>Reads one subject's position on a board. <see cref="LeaderboardRank.Found"/>
        /// false is an answer — the subject has not scored — not an error.</summary>
        Task<LeaderboardRank> GetRankAsync(LeaderboardKey key, string? subjectId,
            CancellationToken cancellationToken);

        /// <summary>Reads the bounded window around one subject's rank. The window is
        /// bounded (1..50).</summary>
        Task<LeaderboardAround> GetAroundMeAsync(LeaderboardKey key, string? subjectId, int window,
            CancellationToken cancellationToken);

        /// <summary>Closes the wire. The session itself survives server-side: a later
        /// resume with the held secret rotates onto a fresh one. The session end that
        /// cannot be resumed is <see cref="LogoutAsync"/>.</summary>
        Task DisconnectAsync();
    }

    /// <summary>
    /// One open subscription, read pull-style: the game advances it from its own loop
    /// (MoveNextAsync on its thread, then hands <see cref="Current"/> to its
    /// <see cref="ISyncApplier"/>), which keeps the game's main-thread state ownership
    /// intact without the SDK ever owning a game-side thread.
    /// </summary>
    public interface ISyncStream
    {
        /// <summary>Advances to the next frame; false when the subscription is over.</summary>
        Task<bool> MoveNextAsync(CancellationToken cancellationToken);

        /// <summary>The frame the last successful advance produced.</summary>
        SyncFrame Current { get; }
    }
}
