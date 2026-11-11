using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The login link (FIX-U3D-07) and the resume link (FIX-U3D-11): mounts through the
    /// platform adapter, opens the transport, exchanges the mount for a session and
    /// starts the gameplay binding — the exact sequence the server's own sample links
    /// drive, so the game code the SDK serves is written once. Every session-security
    /// fact (login attempt id, resume secret) is freshly generated per connect and per
    /// resume; the mount context is consumed exactly once and never reused across
    /// connects, and a resume never touches the adapter at all.
    /// </summary>
    public sealed class ChorusSdk : IChorusSdk
    {
        private readonly Func<SdkConfiguration, IChorusTransport> _transportFactory;

        /// <summary>Creates the SDK over a transport factory. The factory receives the
        /// configuration (it names the endpoint) and answers a fresh transport per
        /// connect; tests inject fakes here. The production factory lives with the
        /// wire it constructs (the edge transport, outside this leaf).</summary>
        public ChorusSdk(Func<SdkConfiguration, IChorusTransport> transportFactory)
        {
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        }

        public async Task<ChorusSession> ConnectAsync(SdkConfiguration configuration, IPlatformAdapter adapter,
            CancellationToken cancellationToken)
        {
            if (configuration is null) throw new ArgumentNullException(nameof(configuration));
            Validate(configuration);
            if (adapter is null) throw new ArgumentNullException(nameof(adapter));

            var mount = await adapter.MountAsync(cancellationToken).ConfigureAwait(false);
            if (mount.Platform != configuration.Platform)
                throw new InvalidOperationException(
                    $"the adapter answered platform '{mount.Platform}' but the configuration names '{configuration.Platform}'.");
            if (mount.Role != configuration.Role)
                throw new InvalidOperationException(
                    $"the adapter answered seat '{mount.Role}' but the configuration names '{configuration.Role}'.");

            var transport = _transportFactory(configuration)
                ?? throw new InvalidOperationException("the transport factory answered nothing.");
            try
            {
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                var intent = BuildIntent(mount);
                var answer = await transport.LoginAsync(intent, cancellationToken).ConfigureAwait(false);
                var bindingId = await transport.StartGameplayAsync(
                    answer.AccessCredential, configuration.GameId, OperationId("mount"),
                    cancellationToken).ConfigureAwait(false);
                // The secret this login registered server-side is the session's own resume
                // credential: the game persists it with the session id for the next resume.
                return new SdkChorusSession(transport, answer, bindingId, intent.NextResumeSecret);
            }
            catch
            {
                // A half-open link never leaks: any failure after the wire opened closes it
                // before the failure surfaces, so the game never holds a dead socket.
                await transport.CloseAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<ChorusSession> ResumeAsync(SdkConfiguration configuration, string previousSessionId,
            byte[] resumeCredential, string? lastSeenRoomId, long lastAppliedStateVersion,
            CancellationToken cancellationToken)
        {
            if (configuration is null) throw new ArgumentNullException(nameof(configuration));
            Validate(configuration);
            if (string.IsNullOrWhiteSpace(previousSessionId))
                throw new ArgumentException("a resume names the session it resumes.", nameof(previousSessionId));
            if (resumeCredential is null || resumeCredential.Length < 32)
                throw new ArgumentException(
                    "a resume presents the 32-byte secret the session was registered with.",
                    nameof(resumeCredential));
            if (lastAppliedStateVersion < 0)
                throw new ArgumentException(
                    "a resume states the version its applier last applied.", nameof(lastAppliedStateVersion));

            var transport = _transportFactory(configuration)
                ?? throw new InvalidOperationException("the transport factory answered nothing.");
            try
            {
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                var secret = NewSecret();
                var answer = await transport.ResumeAsync(new ResumeIntent
                {
                    ResumeOperationId = "resume-" + Guid.NewGuid().ToString("N"),
                    PreviousSessionId = previousSessionId,
                    ResumeCredential = resumeCredential,
                    NextResumeSecret = secret,
                    LastSeenRoomId = string.IsNullOrWhiteSpace(lastSeenRoomId) ? null : lastSeenRoomId,
                    LastAppliedStateVersion = lastAppliedStateVersion,
                }, cancellationToken).ConfigureAwait(false);
                // No gameplay start: the binding lives on the mount record and the resume's
                // re-entry restores it server-side. The fresh secret replaces the one the
                // game persisted — the rotation never reuses a secret.
                return new SdkChorusSession(transport, answer, bindingId: null, secret);
            }
            catch
            {
                await transport.CloseAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static byte[] NewSecret()
        {
            var secret = new byte[32];
            using (var generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(secret);
            }
            return secret;
        }

        private static LoginIntent BuildIntent(PlatformMount mount)
        {
            return new LoginIntent
            {
                Platform = mount.Platform,
                AppId = mount.AppId,
                LaunchCredential = mount.SessionContextJson,
                LoginAttemptId = "attempt-" + Guid.NewGuid().ToString("N"),
                NextResumeSecret = NewSecret(),
            };
        }

        private static string OperationId(string kind) => kind + "-" + Guid.NewGuid().ToString("N");

        /// <summary>The configuration the SDK refuses at once: a missing field or an
        /// unknown seat never reaches the wire.</summary>
        private static void Validate(SdkConfiguration configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration.Platform))
                throw new ArgumentException("the configuration needs the platform name.", nameof(configuration));
            if (string.IsNullOrWhiteSpace(configuration.AppId))
                throw new ArgumentException("the configuration needs the app id.", nameof(configuration));
            if (string.IsNullOrWhiteSpace(configuration.Tenant))
                throw new ArgumentException("the configuration needs the tenant.", nameof(configuration));
            if (string.IsNullOrWhiteSpace(configuration.GameId))
                throw new ArgumentException("the configuration needs the game id.", nameof(configuration));
            if (string.IsNullOrWhiteSpace(configuration.Endpoint))
                throw new ArgumentException("the configuration needs the gateway endpoint.", nameof(configuration));
            if (configuration.Role != "streamer" && configuration.Role != "player")
                throw new ArgumentException(
                    "the configuration seat '" + configuration.Role + "' is not one of streamer, player.",
                    nameof(configuration));
        }
    }
}
