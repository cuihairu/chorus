using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The session the login link answers: the login's identities, the started gameplay
    /// binding, and a transport whose sync and intent links arrive with FIX-U3D-09 and
    /// FIX-U3D-10 — those calls refuse by naming their task, never by failing silently.
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
            throw new InvalidOperationException(
                "the room sync link is not wired yet: subscribe arrives with FIX-U3D-09 " +
                "(the snapshot and delta applier) driving this transport.");

        public Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "the intent link is not wired yet: intent send arrives with FIX-U3D-10.");

        public Task DisconnectAsync() => _transport.CloseAsync();
    }
}
