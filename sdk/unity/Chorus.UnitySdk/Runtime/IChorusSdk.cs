using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The unified Unity SDK entry (FIX-U3D-02): the one surface game code programs
    /// against, on every platform. The platform specifics live behind
    /// <see cref="IPlatformAdapter"/>; the transport (login, subscribe, intents) arrives
    /// with FIX-U3D-07 and the sample links (FIX-U3D-08+) — this contract fixes the shape
    /// they implement, so the game code the SDK serves is written once.
    /// </summary>
    public interface IChorusSdk
    {
        /// <summary>
        /// Mounts through the platform adapter, exchanges the mount for a Chorus session
        /// (login, then gameplay start) and answers the session. The adapter's mount
        /// context is consumed exactly once; every later call rides the session's own
        /// access credential.
        /// </summary>
        Task<ChorusSession> ConnectAsync(SdkConfiguration configuration, IPlatformAdapter adapter,
            CancellationToken cancellationToken);

        /// <summary>
        /// Rotates a dropped session onto a fresh one (FIX-U3D-11): presents the session's
        /// resume secret, answers a new session carrying a new secret to persist, and —
        /// when the server's participation record still holds a round — the room's
        /// re-entry rides <see cref="ChorusSession.AwaitRejoinedRoomAsync"/>, whose
        /// snapshot rebaselines the game's applier. No platform adapter takes part: the
        /// resume never re-redeems the launch credential, the old session's own secret
        /// is the whole proof.
        /// </summary>
        Task<ChorusSession> ResumeAsync(SdkConfiguration configuration, string previousSessionId,
            byte[] resumeCredential, string? lastSeenRoomId, long lastAppliedStateVersion,
            CancellationToken cancellationToken);
    }

    /// <summary>One mounted, logged-in session. The session carries the login's answer and
    /// the gameplay binding, subscribes to rooms and sends intents.</summary>
    public interface ChorusSession
    {
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

        /// <summary>
        /// Subscribes to one room's authoritative stream and answers a pull-style stream the
        /// game drives from its own loop (Unity's main thread owns state, so the SDK never
        /// calls game code from a transport thread). The applied baseline rides the request:
        /// a baseline at the head continues with deltas alone, anything older answers with a
        /// full snapshot first — the same call serves first mount, resume and catch-up
        /// (FIX-U3D-11), exactly like the server's subscribe contract.
        /// </summary>
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

        /// <summary>Answers the room a resume re-entered (FIX-U3D-11): the server's
        /// participation record names it, and the head snapshot behind the entry is the
        /// re-baseline the game's applier continues from. A resume with nowhere to
        /// return announces nothing on the wire, so the game bounds this wait itself —
        /// a quiet budget means the round was already over. A seat whose round stands
        /// parked (FIX-U3D-12) gets the park notice as this call's refusal instead: an
        /// <see cref="EdgeProtocolException"/> coded roomSuspended, carrying the reason
        /// and the epoch the recovery will land on — the game re-enters on a later
        /// resume once the round runs again.</summary>
        Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken);

        /// <summary>Re-declares the gameplay binding on the open wire (FIX-U3D-12): the
        /// startGameplay the login already ran, sent again. It is the resume-ready
        /// declaration (FIX-SYNC-08): while the seat's own round stands paused, the
        /// mount refusal the call raises IS the readiness report — the store counts the
        /// seat and the coordinator resumes the round once enough seats stand ready.
        /// The same call re-opens the door after a release: it answers a fresh binding
        /// and the wire re-enters any room the mount holds (the entry rides
        /// <see cref="AwaitRejoinedRoomAsync"/>). The refusal's code is the wire's one
        /// word for every precondition failure; the game tells a park from a
        /// displacement by the stream it was reading.</summary>
        Task<string> RedeclareGameplayAsync(string gameId, CancellationToken cancellationToken);

        /// <summary>Withdraws the seat from one round — the normal exit from play
        /// (FIX-U3D-12). The wire acknowledges no leave: the call completes when the
        /// frame is sent, and the room's frames for this seat stop. A refusal (a room
        /// this mount is not in) names the operation id on the stream and ends it.</summary>
        Task LeaveRoundAsync(string roomId, CancellationToken cancellationToken);

        /// <summary>Ends the session for good — the normal exit from the service
        /// (FIX-U3D-12): the logout revokes the session durably before the gateway
        /// closes, and a later resume with the held secret is refused as revoked. The
        /// wire acknowledges no logout; the SDK sends the frame and closes its own side.
        /// A mere <see cref="DisconnectAsync"/> keeps the session resumable — this one
        /// does not.</summary>
        Task LogoutAsync(CancellationToken cancellationToken);

        /// <summary>Reads one page of a board's TopN (FIX-U3D-13): the board's declared
        /// order (score descending, subject id ascending) at one read view, bounded and
        /// paginated. The tenant and the game are the session's own facts — the key names
        /// only board/period/scope/subject. <paramref name="cursor"/> continues the walk a
        /// previous page's <see cref="LeaderboardTop.NextCursor"/> opened; a continuation
        /// whose board changed or whose snapshot expired throws
        /// <see cref="LeaderboardStaleException"/> — restart from a cursor-less call,
        /// never stitch pages across versions.</summary>
        Task<LeaderboardTop> GetTopAsync(LeaderboardKey key, int maxEntries, string? cursor,
            CancellationToken cancellationToken);

        /// <summary>Reads one subject's position on a board (FIX-U3D-13): its unique
        /// ordinal rank and its score, never from different versions.
        /// <paramref name="subjectId"/> null reads this session's own identity;
        /// <see cref="LeaderboardRank.Found"/> false is an answer — the subject has not
        /// scored — not an error.</summary>
        Task<LeaderboardRank> GetRankAsync(LeaderboardKey key, string? subjectId,
            CancellationToken cancellationToken);

        /// <summary>Reads the bounded window around one subject's rank (FIX-U3D-13): the
        /// subject plus this many neighbours on each side, in rank order, at one read
        /// view. <paramref name="subjectId"/> null centres the window on this session's
        /// own identity; a subject that has not scored answers
        /// <see cref="LeaderboardAround.Found"/> false. The window is bounded (1..50).</summary>
        Task<LeaderboardAround> GetAroundMeAsync(LeaderboardKey key, string? subjectId, int window,
            CancellationToken cancellationToken);

        /// <summary>Closes the wire. The session itself survives server-side: a later
        /// resume with the held secret rotates onto a fresh one (FIX-U3D-11). The session
        /// end that cannot be resumed is <see cref="LogoutAsync"/>.</summary>
        Task DisconnectAsync();
    }

    /// <summary>
    /// One open subscription, read pull-style: the game advances it from its own loop
    /// (MoveNextAsync on its thread, then hands <see cref="Current"/> to its
    /// <see cref="ISyncApplier"/>), which keeps Unity's main-thread state ownership intact
    /// without the SDK ever owning a game-side thread.
    /// </summary>
    public interface ISyncStream
    {
        /// <summary>Advances to the next frame; false when the subscription is over.</summary>
        Task<bool> MoveNextAsync(CancellationToken cancellationToken);

        /// <summary>The frame the last successful advance produced.</summary>
        SyncFrame Current { get; }
    }
}
