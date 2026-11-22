using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.GodotSdk
{
    /// <summary>
    /// The session the login link answers (EK-1): the login's identities, the started
    /// gameplay binding, the room's sync stream and the intent send, the match leg, the
    /// resume leg's re-entry, the leaderboard reads and the lifecycle legs. Disconnect
    /// closes the wire; logout ends the session.
    ///
    /// The event surface the task book asks for is carried here as plain C# events,
    /// raised as the wire's facts land: a Godot <c>Node</c> bridge subscribes and
    /// re-emits them as Godot signals, so the leaf stays engine-free and its test lane
    /// stays pure .NET. The login and resume facts are not events — they are already in
    /// hand when this session exists (<see cref="Login"/>, and the SDK's
    /// <see cref="ChorusGodotSdk.SessionEstablished"/> names the session); match, room
    /// and disconnect facts land later, so they ride events.
    /// </summary>
    public sealed class GodotChorusSession : ChorusSession
    {
        private readonly IChorusTransport _transport;

        /// <summary>Fires when the queue committed the match: the ticket is Matched and the
        /// room that names it is on the ticket. The seat's room facts follow on
        /// <see cref="AwaitRoomAsync"/>'s <see cref="RoomJoined"/>.</summary>
        public event Action<MatchTicket>? MatchJoined;

        /// <summary>Fires when the ticket was withdrawn and the queue answered it.</summary>
        public event Action<MatchTicket>? MatchCancelled;

        /// <summary>Fires when the seat's room entry answers: the committed room plus the
        /// seat's room facts together. On a resume this is the re-entry the
        /// <see cref="AwaitRejoinedRoomAsync"/> leg answers.</summary>
        public event Action<JoinedRoom>? RoomJoined;

        /// <summary>Fires when the seat leaves a room; the argument is the room id.</summary>
        public event Action<string>? RoomLeft;

        /// <summary>Fires when the wire closes — a disconnect or a logout. A disconnect
        /// leaves the session resumable; a logout does not.</summary>
        public event Action? Disconnected;

        internal GodotChorusSession(IChorusTransport transport, LoginAnswer login, string? bindingId,
            byte[] resumeCredential)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Login = login ?? throw new ArgumentNullException(nameof(login));
            BindingId = bindingId;
            ResumeCredential = resumeCredential ?? throw new ArgumentNullException(nameof(resumeCredential));
        }

        public LoginAnswer Login { get; }

        public string? BindingId { get; }

        public byte[] ResumeCredential { get; }

        public Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken) =>
            _transport.SubscribeAsync(roomId, epoch, lastAppliedTick, lastAppliedStateVersion, cancellationToken);

        public Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken)
        {
            if (intent is null) throw new ArgumentNullException(nameof(intent));
            if (string.IsNullOrWhiteSpace(intent.RoomId))
                throw new ArgumentException("An intent names the room it plays in.", nameof(intent));
            if (intent.RoomEpoch <= 0)
                throw new ArgumentException("An intent names its room's epoch.", nameof(intent));
            if (string.IsNullOrWhiteSpace(intent.CommandId))
                throw new ArgumentException(
                    "An intent carries the caller's idempotency key; the SDK never invents one.", nameof(intent));
            return _transport.SendIntentAsync(intent, cancellationToken);
        }

        public async Task<MatchTicket> JoinMatchAsync(string queueType, string? options,
            CancellationToken cancellationToken)
        {
            var ticket = await _transport.JoinMatchAsync(new JoinMatchIntent
            {
                OperationId = "join-" + Guid.NewGuid().ToString("N"),
                QueueType = queueType,
                Options = options ?? string.Empty,
            }, cancellationToken).ConfigureAwait(false);
            // Matched is the atomic commit: the ticket names the room. Queued answers the
            // queue's own word; the room entry rides AwaitRoomAsync.
            if (ticket.State == "Matched")
            {
                MatchJoined?.Invoke(ticket);
            }
            return ticket;
        }

        public async Task<MatchTicket> CancelMatchAsync(string ticketId, CancellationToken cancellationToken)
        {
            var ticket = await _transport.CancelMatchAsync("cancel-" + Guid.NewGuid().ToString("N"),
                ticketId, cancellationToken).ConfigureAwait(false);
            if (ticket.State == "Cancelled")
            {
                MatchCancelled?.Invoke(ticket);
            }
            return ticket;
        }

        public async Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken)
        {
            var room = await _transport.AwaitRoomAsync(cancellationToken).ConfigureAwait(false);
            RoomJoined?.Invoke(room);
            return room;
        }

        public async Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken)
        {
            var room = await _transport.AwaitRejoinedRoomAsync(cancellationToken).ConfigureAwait(false);
            RoomJoined?.Invoke(room);
            return room;
        }

        public Task<string> RedeclareGameplayAsync(string gameId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new ArgumentException("A redeclaration names the game it mounts.", nameof(gameId));
            return _transport.StartGameplayAsync(Login.AccessCredential, gameId,
                "attach-" + Guid.NewGuid().ToString("N"), cancellationToken);
        }

        public async Task LeaveRoundAsync(string roomId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                throw new ArgumentException("A leave names the room it withdraws from.", nameof(roomId));
            await _transport.LeaveRoundAsync(roomId, "leave-" + Guid.NewGuid().ToString("N"), cancellationToken)
                .ConfigureAwait(false);
            RoomLeft?.Invoke(roomId);
        }

        public async Task LogoutAsync(CancellationToken cancellationToken)
        {
            await _transport.LogoutAsync("logout-" + Guid.NewGuid().ToString("N"), cancellationToken)
                .ConfigureAwait(false);
            Disconnected?.Invoke();
        }

        public async Task<LeaderboardTop> GetTopAsync(LeaderboardKey key, int maxEntries, string? cursor,
            CancellationToken cancellationToken)
        {
            if (key is null || string.IsNullOrWhiteSpace(key.BoardId))
                throw new ArgumentException("A top page names the board it walks.", nameof(key));
            var answer = await _transport.LeaderboardQueryAsync(new LeaderboardQueryIntent
            {
                OperationId = "lb-" + Guid.NewGuid().ToString("N"),
                Kind = "top",
                BoardId = key.BoardId,
                PeriodId = key.PeriodId,
                ScopeId = key.ScopeId,
                Subject = (int)key.Subject,
                MaxEntries = maxEntries,
                Cursor = cursor,
            }, cancellationToken).ConfigureAwait(false);
            return TopFrom(answer);
        }

        public async Task<LeaderboardRank> GetRankAsync(LeaderboardKey key, string? subjectId,
            CancellationToken cancellationToken)
        {
            var answer = await ReadPositionAsync(key, subjectId, 0, "rank", cancellationToken)
                .ConfigureAwait(false);
            var ranked = answer.Entries.Count > 0 ? answer.Entries[0] : null;
            return new LeaderboardRank
            {
                Found = answer.Found,
                Rank = ranked?.Rank ?? 0,
                SubjectId = ranked?.SubjectId ?? string.Empty,
                Score = ranked?.Score ?? 0,
                ReadVersion = answer.ReadVersion,
            };
        }

        public async Task<LeaderboardAround> GetAroundMeAsync(LeaderboardKey key, string? subjectId,
            int window, CancellationToken cancellationToken)
        {
            var answer = await ReadPositionAsync(key, subjectId, window, "around", cancellationToken)
                .ConfigureAwait(false);
            var around = new LeaderboardAround
            {
                Found = answer.Found,
                ReadVersion = answer.ReadVersion,
            };
            var entries = new List<LeaderboardRankedEntry>();
            foreach (var entry in answer.Entries)
            {
                entries.Add(new LeaderboardRankedEntry
                {
                    Rank = entry.Rank,
                    SubjectId = entry.SubjectId,
                    Score = entry.Score,
                });
            }
            around.Entries = entries;
            return around;
        }

        /// <summary>The shared rank/around leg: the two reads differ only in the wire's
        /// kind word and the window; the mapping onto the leaf shapes reads the answer's
        /// own shape (a rank's single entry rides the entries list, an around's window
        /// rides it whole).</summary>
        private Task<LeaderboardAnswer> ReadPositionAsync(LeaderboardKey key, string? subjectId,
            int window, string kind, CancellationToken cancellationToken)
        {
            if (key is null || string.IsNullOrWhiteSpace(key.BoardId))
                throw new ArgumentException("A position read names the board it queries.", nameof(key));
            if (subjectId is { Length: 0 })
                throw new ArgumentException("A subject id names its subject or stays null.",
                    nameof(subjectId));
            return _transport.LeaderboardQueryAsync(new LeaderboardQueryIntent
            {
                OperationId = "lb-" + Guid.NewGuid().ToString("N"),
                Kind = kind,
                BoardId = key.BoardId,
                PeriodId = key.PeriodId,
                ScopeId = key.ScopeId,
                Subject = (int)key.Subject,
                SubjectId = subjectId,
                Window = window,
            }, cancellationToken);
        }

        private static LeaderboardTop TopFrom(LeaderboardAnswer answer)
        {
            var top = new LeaderboardTop
            {
                ReadVersion = answer.ReadVersion,
                NextCursor = answer.NextCursor,
                HasMore = answer.HasMore,
            };
            var entries = new List<LeaderboardEntry>();
            foreach (var entry in answer.Entries)
            {
                entries.Add(new LeaderboardEntry { SubjectId = entry.SubjectId, Score = entry.Score });
            }
            top.Entries = entries;
            return top;
        }

        public async Task DisconnectAsync()
        {
            await _transport.CloseAsync().ConfigureAwait(false);
            Disconnected?.Invoke();
        }
    }
}
