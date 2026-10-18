using Chorus.Control;
using Chorus.Data;
using Chorus.Engine.Abstractions;

namespace Chorus.EngineHost;

/// <summary>Fail-closed validation and restore path for durable control-plane checkpoints.</summary>
public static class ControlCheckpointRestorer
{
    public static void Restore(IGameModule module, RoomRecord room)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(room);
        if (room.Snapshot is not { } snapshot) return;
        if (room.SchemaVersion != 1 || SnapshotIntegrity.Compute(snapshot.GetRawText()) != room.Checksum)
            throw new InvalidOperationException("Corrupt or incompatible controlled checkpoint.");
        module.RestoreState(room.SchemaVersion,snapshot);
    }
}
