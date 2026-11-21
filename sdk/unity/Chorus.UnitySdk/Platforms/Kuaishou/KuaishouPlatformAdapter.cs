using System;
using System.Threading;
using System.Threading.Tasks;
using Chorus.UnitySdk;

namespace Chorus.UnitySdk.Platforms.Kuaishou
{
    /// <summary>
    /// The Kuaishou platform adapter (FIX-U3D-06): IPlatformAdapter over the PC
    /// companion's launch handshake, via <see cref="IKuaishouSdkBridge"/>. The adapter
    /// answers the streamer seat — the game is the streamer's exe the companion
    /// launches; viewers participate through the server interaction push the Chorus
    /// platform layer ingests (bind + gift top + push are the launch-required set),
    /// never through a per-viewer client mount.
    ///
    /// This file carries no native IPC references: the platform-neutral mapping
    /// compiles everywhere (the dotnet build, and Unity without the library where the
    /// asmdef's defineConstraint and Windows-only includePlatforms keep the whole
    /// assembly out anyway), and the real IPC call sites land inside the bridge when
    /// the library and grant arrive.
    /// </summary>
    public sealed class KuaishouPlatformAdapter : IPlatformAdapter
    {
        private readonly string _appId;
        private readonly IKuaishouSdkBridge _bridge;

        /// <summary>Creates the adapter over a bridge. Game code uses the unavailable
        /// bridge until the real one ships; tests inject fakes.</summary>
        public KuaishouPlatformAdapter(string appId, IKuaishouSdkBridge bridge)
        {
            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentException("a kuaishou mount needs the app id from the platform.", nameof(appId));
            _appId = appId;
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        /// <summary>The platform name the adapter answers for.</summary>
        public string PlatformName => "kuaishou";

        /// <summary>Runs the launch handshake and maps it to the unified mount answer.</summary>
        public async Task<PlatformMount> MountAsync(CancellationToken cancellationToken)
        {
            var launch = await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            return new PlatformMount
            {
                Platform = PlatformName,
                AppId = _appId,
                // Kuaishou hands no client-side streamer open id — the room code is the
                // platform-side handle the companion (the streamer's authenticated
                // session) passed at launch, and the server resolves the streamer
                // identity through its bind call carrying the same room code.
                StreamerId = launch.RoomCode,
                // The streamer seat: kuaishou mounts never claim a player identity —
                // viewers ride the platform's server interaction push.
                UserId = string.Empty,
                Role = "streamer",
                SessionContextJson = launch.LaunchCredentialJson,
            };
        }

        /// <summary>Releases the IPC lifecycle.</summary>
        public Task ShutdownAsync() => _bridge.ShutdownAsync();
    }
}
