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

        /// <summary>Closes the wire. A closed transport answers nothing further.</summary>
        Task CloseAsync();
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
