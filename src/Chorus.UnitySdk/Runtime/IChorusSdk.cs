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
        /// a quiet budget means the round was already over.</summary>
        Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken);

        /// <summary>Client-initiated session end: revokes the session and closes the transport.</summary>
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
