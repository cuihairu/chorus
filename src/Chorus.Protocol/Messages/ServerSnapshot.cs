using System.Text.Json;

namespace Chorus.Protocol.Messages;

public sealed record ServerSnapshot(
    string Type,
    string SessionId,
    long TickId,
    long ServerTimeMs,
    JsonElement State,
    bool? Forced = null,
    string? Reason = null,
    string? TargetClientId = null,
    long? ClientLagTicks = null,
    long? ClientLastAckTickId = null,
    /// <summary>Snapshot schema version (versioning.md §2, "1.0 计划" first half):
    /// stamped by the engine snapshot and relayed unchanged; wire name "v".
    /// Defaults to 1 so an older sender's missing field still reads as v1.
    /// Unknown-version rejection lands when a v2 is designed (1.0).</summary>
    int V = 1
);
