using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk.Platforms.Douyin
{
    /// <summary>
    /// The seam between the Douyin adapter and the official LiveOpenSDK (FIX-U3D-01's
    /// layer 1): the adapter's mount mapping is platform-neutral C# and fully tested
    /// here, while the SDK's own call sites hide behind this interface. The real bridge
    /// lands with the official SDK package and the Apply grant (the admission matrix's
    /// needs-permission rows; FIX-ENV-05 records the admission, FIX-U3D-17 drives the
    /// real link) — until then the only shipped bridge is
    /// <see cref="DouyinSdkUnavailableBridge"/>, which refuses with the reason.
    ///
    /// The official integration points, from the documented product shape
    /// (docs/reference/platform-unity-sdks.md §1): the SDK start flow consumes the
    /// app id configured in the package, the live-info API answers the room id and
    /// the streamer's open id, and the launch credential the SDK produces is the
    /// session context the Chorus server verifies before issuing its own authorization.
    /// </summary>
    public interface IDouyinSdkBridge
    {
        /// <summary>Runs the SDK's launch flow and answers what the mount needs.</summary>
        Task<DouyinLaunch> StartAsync(CancellationToken cancellationToken);

        /// <summary>Releases the SDK lifecycle.</summary>
        Task ShutdownAsync();
    }

    /// <summary>What one Douyin launch produced: who is mounting and what the platform handed over.</summary>
    public sealed class DouyinLaunch
    {
        /// <summary>The streamer's platform open id from the live-info API.</summary>
        public string StreamerOpenId { get; set; } = string.Empty;

        /// <summary>The live room the launch rides (diagnosis and cross-checks; the Chorus room is established server-side).</summary>
        public string RoomId { get; set; } = string.Empty;

        /// <summary>The launch credential the SDK produced, as the platform gave it — the mount's session context.</summary>
        public string LaunchCredentialJson { get; set; } = string.Empty;
    }

    /// <summary>
    /// The only bridge that can ship without credentials: it refuses every launch with
    /// the reason the admission matrix states. Game code sees the refusal through the
    /// adapter's mount failure — never a silently empty mount.
    /// </summary>
    public sealed class DouyinSdkUnavailableBridge : IDouyinSdkBridge
    {
        public Task<DouyinLaunch> StartAsync(CancellationToken cancellationToken) =>
            Task.FromException<DouyinLaunch>(new InvalidOperationException(
                "the official Douyin LiveOpenSDK is not integrated: the package and the " +
                "Apply internal-test grant are platform-side steps (admission matrix, FIX-ENV-05)"));

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
