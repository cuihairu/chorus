using System.Text.Json;

namespace Chorus.Engine.Abstractions;

/// <summary>
/// An authoritative snapshot handed to a module at session birth. Version is
/// the wire snapshot schema version (<c>ServerSnapshot.v</c>); State is the
/// wire-format JSON the module's <c>GetStateSnapshot()</c> once produced.
/// </summary>
public sealed record SnapshotRecovery(long Version, JsonElement State);

/// <summary>
/// Source of authoritative snapshots at session birth (todo §10 restore half).
/// <see cref="TryGet"/> returning false means "no snapshot for this session" -
/// a fresh start, exactly today's behavior. The persistence medium and write
/// timing are a separate, later decision (snapshot-restore.md「明确不做」边界):
/// production wiring follows the topology work; tests supply in-memory sources.
/// </summary>
public interface ISnapshotRecoverySource
{
    bool TryGet(string sessionId, out SnapshotRecovery recovery);
}
