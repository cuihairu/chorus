using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The session the login link answers: the login's identities, the started gameplay
    /// binding, the room's sync stream (FIX-U3D-09), the match leg, and a transport whose
    /// intent link arrives with FIX-U3D-10 — that call refuses by naming its task, never
    /// by failing silently.
    /// </summary>
    internal sealed class SdkChorusSession : ChorusSession
    {
        private readonly IChorusTransport _transport;

        internal SdkChorusSession(IChorusTransport transport, LoginAnswer login, string bindingId)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Login = login ?? throw new ArgumentNullException(nameof(login));
            BindingId = bindingId ?? throw new ArgumentNullException(nameof(bindingId));
        }

        public LoginAnswer Login { get; }

        public string BindingId { get; }

        public Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken) =>
            _transport.SubscribeAsync(roomId, epoch, lastAppliedTick, lastAppliedStateVersion, cancellationToken);

        public Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "the intent link is not wired yet: intent send arrives with FIX-U3D-10.");

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

        public Task DisconnectAsync() => _transport.CloseAsync();
    }
}
