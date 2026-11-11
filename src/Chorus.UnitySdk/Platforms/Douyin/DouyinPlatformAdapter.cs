using System;
using System.Threading;
using System.Threading.Tasks;
using Chorus.UnitySdk;

namespace Chorus.UnitySdk.Platforms.Douyin
{
    /// <summary>
    /// The Douyin platform adapter (FIX-U3D-05): IPlatformAdapter over the official
    /// LiveOpenSDK's launch flow, via <see cref="IDouyinSdkBridge"/>. The adapter answers
    /// the streamer seat — Douyin's Unity gameplay is the streamer's client; viewers
    /// participate through the platform's server-side events the Chorus platform layer
    /// ingests, never through a per-viewer client mount.
    ///
    /// This file carries no ByteDance.* references: the platform-neutral mapping
    /// compiles everywhere (the dotnet build, and Unity without the SDK where the
    /// asmdef's defineConstraint keeps the whole assembly out anyway), and the real
    /// SDK call sites land inside the bridge when the package and grant arrive.
    /// </summary>
    public sealed class DouyinPlatformAdapter : IPlatformAdapter
    {
        private readonly string _appId;
        private readonly IDouyinSdkBridge _bridge;

        /// <summary>Creates the adapter over a bridge. Game code uses the unavailable
        /// bridge until the real one ships; tests inject fakes.</summary>
        public DouyinPlatformAdapter(string appId, IDouyinSdkBridge bridge)
        {
            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentException("a douyin mount needs the app id from the platform.", nameof(appId));
            _appId = appId;
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        /// <summary>The platform name the adapter answers for.</summary>
        public string PlatformName => "douyin";

        /// <summary>Runs the SDK launch and maps it to the unified mount answer.</summary>
        public async Task<PlatformMount> MountAsync(CancellationToken cancellationToken)
        {
            var launch = await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            return new PlatformMount
            {
                Platform = PlatformName,
                AppId = _appId,
                StreamerId = launch.StreamerOpenId,
                // The streamer seat: douyin mounts never claim a player identity —
                // viewers ride the platform's server-side events.
                UserId = string.Empty,
                Role = "streamer",
                SessionContextJson = launch.LaunchCredentialJson,
            };
        }

        /// <summary>Releases the SDK lifecycle.</summary>
        public Task ShutdownAsync() => _bridge.ShutdownAsync();
    }
}
