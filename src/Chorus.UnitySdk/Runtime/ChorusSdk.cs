using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The login link (FIX-U3D-07): mounts through the platform adapter, opens the
    /// transport, exchanges the mount for a session and starts the gameplay binding —
    /// the exact sequence the server's own sample links drive, so the game code the
    /// SDK serves is written once. Every session-security fact (login attempt id,
    /// resume secret) is freshly generated per connect; the mount context is consumed
    /// exactly once and never reused across connects.
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
                var answer = await transport.LoginAsync(BuildIntent(mount), cancellationToken).ConfigureAwait(false);
                var bindingId = await transport.StartGameplayAsync(
                    answer.AccessCredential, configuration.GameId, OperationId("mount"),
                    cancellationToken).ConfigureAwait(false);
                return new SdkChorusSession(transport, answer, bindingId);
            }
            catch
            {
                // A half-open link never leaks: any failure after the wire opened closes it
                // before the failure surfaces, so the game never holds a dead socket.
                await transport.CloseAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static LoginIntent BuildIntent(PlatformMount mount)
        {
            var secret = new byte[32];
            using (var generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(secret);
            }
            return new LoginIntent
            {
                Platform = mount.Platform,
                AppId = mount.AppId,
                LaunchCredential = mount.SessionContextJson,
                LoginAttemptId = "attempt-" + Guid.NewGuid().ToString("N"),
                NextResumeSecret = secret,
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
