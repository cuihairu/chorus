using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The seam the platform adapters implement (FIX-U3D-05 Douyin, FIX-U3D-06 Kuaishou):
    /// turn the platform's own launch flow into one mount answer the unified SDK consumes.
    /// The Douyin adapter wraps the official LiveOpenSDK (a managed assembly behind its own
    /// conditional-compilation symbol); the Kuaishou adapter parses the companion's launch
    /// parameters and speaks the IPC library (a native library, Windows-only). Game code
    /// never sees either namespace — it only ever holds this interface.
    /// </summary>
    public interface IPlatformAdapter
    {
        /// <summary>The platform name the adapter answers for ("douyin", "kuaishou").</summary>
        string PlatformName { get; }

        /// <summary>
        /// Runs the platform's launch flow and answers who is mounting. The session context
        /// is platform-owned material (a Douyin launch credential, a Kuaishou roomCode and
        /// companion launch parameters): the adapter hands it to the SDK once, and the SDK
        /// exchanges it for a Chorus session — the server verifies the platform fact and
        /// issues its own authorization.
        /// </summary>
        Task<PlatformMount> MountAsync(CancellationToken cancellationToken);

        /// <summary>Releases the platform resources (the Douyin SDK lifecycle, the Kuaishou IPC pipe).</summary>
        Task ShutdownAsync();
    }

    /// <summary>One platform mount: who is playing, from which seat, carrying what session context.</summary>
    public sealed class PlatformMount
    {
        /// <summary>The platform the mount came from (must equal the adapter's PlatformName).</summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>The platform app id the mount was issued under.</summary>
        public string AppId { get; set; } = string.Empty;

        /// <summary>The platform open id of the streamer the room rides.</summary>
        public string StreamerId { get; set; } = string.Empty;

        /// <summary>The platform open id of the viewer taking a player seat (empty for a streamer mount).</summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>Which seat the mount claims: "streamer" or "player".</summary>
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// The platform's own session context, kept as the platform produced it (JSON). It is
        /// runtime session material — not configuration, and never logged whole.
        /// </summary>
        public string SessionContextJson { get; set; } = string.Empty;
    }
}
