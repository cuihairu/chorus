using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk.Platforms.Kuaishou
{
    /// <summary>
    /// The seam between the Kuaishou adapter and the platform's companion IPC library
    /// (FIX-U3D-01's layer 1): the adapter's mount mapping is platform-neutral C# and
    /// fully tested here, while the IPC call sites (InitIpc/SendData with the -ipc
    /// channel name, the data/connected/disconnect callbacks) hide behind this
    /// interface. Kuaishou ships no official Unity gift SDK — the game is a standalone
    /// Windows exe the PC companion launches, and the only client-side channel is
    /// companion cooperation. Gifts, comments and likes never ride IPC: they arrive on
    /// the server push leg the Chorus platform layer ingests.
    ///
    /// The real bridge lands with the IPC library and the joint-debugging scope grant
    /// (the admission matrix's needs-permission rows; FIX-ENV-06 records the
    /// admission) — until then the only shipped bridge is
    /// <see cref="KuaishouSdkUnavailableBridge"/>, which refuses with the reason.
    /// </summary>
    public interface IKuaishouSdkBridge
    {
        /// <summary>Runs the launch handshake — reads the companion's launch arguments
        /// and opens the IPC channel — and answers what the mount needs.</summary>
        Task<KuaishouLaunch> StartAsync(CancellationToken cancellationToken);

        /// <summary>Releases the IPC lifecycle.</summary>
        Task ShutdownAsync();
    }

    /// <summary>What one Kuaishou launch produced: the room handle the companion handed
    /// over and the IPC channel the game rides.</summary>
    public sealed class KuaishouLaunch
    {
        /// <summary>The live room code — from the -c launch argument, or pushed later
        /// over IPC when the manifest opts into ipcSetCode and the stream started
        /// after the game did.</summary>
        public string RoomCode { get; set; } = string.Empty;

        /// <summary>The -ipc channel name the game opened as CLIENT; empty when the
        /// manifest opts out of both ipcSetCode and ipcQuit.</summary>
        public string IpcChannelName { get; set; } = string.Empty;

        /// <summary>The launch facts as the platform gave them (room code, ipc
        /// channel, window rect) — the mount's session context the Chorus server
        /// cross-checks against its bind call.</summary>
        public string LaunchCredentialJson { get; set; } = string.Empty;
    }

    /// <summary>
    /// The only bridge that can ship without credentials: it refuses every launch with
    /// the reason the admission matrix states. Game code sees the refusal through the
    /// adapter's mount failure — never a silently empty mount.
    /// </summary>
    public sealed class KuaishouSdkUnavailableBridge : IKuaishouSdkBridge
    {
        public Task<KuaishouLaunch> StartAsync(CancellationToken cancellationToken) =>
            Task.FromException<KuaishouLaunch>(new InvalidOperationException(
                "the official Kuaishou companion IPC is not integrated: the ipc library " +
                "package and the joint-debugging scope grant are platform-side steps " +
                "(admission matrix, FIX-ENV-06)"));

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
