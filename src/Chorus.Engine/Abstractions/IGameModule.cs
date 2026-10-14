using System.Text.Json;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Abstractions;

public interface IGameModule : IDisposable
{
    string Name { get; }

    void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events);

    object GetStateSnapshot();

    /// <summary>
    /// Rebuilds module state from an authoritative snapshot (snapshot-restore.md
    /// 方案 A, 2026-10-14 拍板): called once at session birth, before any
    /// <see cref="ApplyEvents"/> can run. The input is the wire-format JSON
    /// (<see cref="GetStateSnapshot"/> output round-trips through it); throwing
    /// on an unknown <paramref name="version"/> or malformed state refuses the
    /// session (fail-closed) - implementations must never serve half-restored
    /// state.
    /// </summary>
    void RestoreState(long version, JsonElement state);
}
