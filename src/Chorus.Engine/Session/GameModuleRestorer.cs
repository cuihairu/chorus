using System.Text.Json;
using Chorus.Engine.Abstractions;

namespace Chorus.Engine.Session;

/// <summary>
/// Session refused to start because its authoritative snapshot could not be
/// restored (invalid/unknown version, oversize state, module failure) -
/// fail-closed: serving from a half-restored state is worse than refusing.
/// </summary>
public sealed class SnapshotRestoreException : InvalidOperationException
{
    public SnapshotRestoreException(string sessionId, string message, Exception? inner = null)
        : base(message, inner)
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }
}

/// <summary>
/// Single-point restore step shared by both session birth points (monolith
/// <see cref="Chorus.Server.SessionManager"/> / split <see cref="Chorus.EngineHost.EngineIpcService"/>)
/// - parity by construction (snapshot-restore.md §7.6). Runs inside the actor
/// factory, after module creation and before the <see cref="SessionActor"/>
/// exists, so no tick and no ingress event can ever touch an unrestored
/// module: the actor simply does not exist yet. Any failure propagates out of
/// the factory; the registry counts it in <see cref="SessionRegistry.StartFailures"/>
/// and never inserts a half state.
/// </summary>
public static class GameModuleRestorer
{
    public static void Restore(IGameModule module, string sessionId, ISnapshotRecoverySource? source, SessionConfig config)
    {
        if (source is null || !source.TryGet(sessionId, out var recovery))
        {
            return;
        }

        if (recovery.Version < 1)
        {
            throw new SnapshotRestoreException(sessionId, $"snapshot version {recovery.Version} is invalid");
        }

        // Same gate as the tick-side snapshot path (SessionActor): the wire
        // state must fit the configured cap before it is applied.
        if (config.MaxSnapshotBytes > 0)
        {
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(recovery.State);
            if (stateBytes.Length > config.MaxSnapshotBytes)
            {
                throw new SnapshotRestoreException(
                    sessionId,
                    $"recovered snapshot {stateBytes.Length}B exceeds cap {config.MaxSnapshotBytes}B");
            }
        }

        try
        {
            module.RestoreState(recovery.Version, recovery.State);
        }
        catch (Exception ex)
        {
            // Unknown schema version, malformed state, module bug - anything:
            // the session must not start.
            throw new SnapshotRestoreException(
                sessionId,
                $"module {module.Name} failed to restore from snapshot version {recovery.Version}",
                ex);
        }
    }
}
