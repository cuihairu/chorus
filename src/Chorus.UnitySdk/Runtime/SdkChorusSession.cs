using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The session the login link answers: the login's identities, the started gameplay
    /// binding, the room's sync stream and the intent send (FIX-U3D-09/10), the match
    /// leg, the resume leg's re-entry (FIX-U3D-11), and the lifecycle legs — the
    /// resume-ready redeclaration, the round leave and the logout (FIX-U3D-12).
    /// Disconnect closes the wire; logout ends the session.
    /// </summary>
    internal sealed class SdkChorusSession : ChorusSession
    {
        private readonly IChorusTransport _transport;

        internal SdkChorusSession(IChorusTransport transport, LoginAnswer login, string? bindingId,
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

        public Task<MatchTicket> JoinMatchAsync(string queueType, string? options,
            CancellationToken cancellationToken) =>
            _transport.JoinMatchAsync(new JoinMatchIntent
            {
                OperationId = "join-" + Guid.NewGuid().ToString("N"),
                QueueType = queueType,
                Options = options ?? string.Empty,
            }, cancellationToken);

        public Task<MatchTicket> CancelMatchAsync(string ticketId, CancellationToken cancellationToken) =>
            _transport.CancelMatchAsync("cancel-" + Guid.NewGuid().ToString("N"), ticketId, cancellationToken);

        public Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken) =>
            _transport.AwaitRoomAsync(cancellationToken);

        public Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken) =>
            _transport.AwaitRejoinedRoomAsync(cancellationToken);

        public Task<string> RedeclareGameplayAsync(string gameId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new ArgumentException("A redeclaration names the game it mounts.", nameof(gameId));
            return _transport.StartGameplayAsync(Login.AccessCredential, gameId,
                "attach-" + Guid.NewGuid().ToString("N"), cancellationToken);
        }

        public Task LeaveRoundAsync(string roomId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                throw new ArgumentException("A leave names the room it withdraws from.", nameof(roomId));
            return _transport.LeaveRoundAsync(roomId, "leave-" + Guid.NewGuid().ToString("N"), cancellationToken);
        }

        public Task LogoutAsync(CancellationToken cancellationToken) =>
            _transport.LogoutAsync("logout-" + Guid.NewGuid().ToString("N"), cancellationToken);

        public Task DisconnectAsync() => _transport.CloseAsync();
    }
}
