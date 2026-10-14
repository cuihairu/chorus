using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Chorus.Data;

/// <summary>
/// 持久化快照记录（审计「持久化快照必须有版本与完整性校验」）。The record is
/// canonical: the store recomputes <see cref="Checksum"/> from the payload, so a
/// caller can never present a self-certified checksum.
/// </summary>
public sealed record StateSnapshot(
    string RoomId,
    long Version,
    int SchemaVersion,
    string Payload,
    long Checksum);

/// <summary>
/// Snapshot integrity (audit: 持久化快照必须有完整性校验). SHA-256 over UTF-8
/// payload, first 8 bytes as a big-endian long - deterministic across processes
/// and versions, so a snapshot can be verified offline before a restore is
/// attempted.
/// </summary>
public static class SnapshotIntegrity
{
    public static long Compute(string? payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(hash.AsSpan(0, 8));
    }

    /// <summary>True when the record's checksum still matches its payload.</summary>
    public static bool Verify(StateSnapshot? snapshot) =>
        snapshot is not null && snapshot.Checksum == Compute(snapshot.Payload);
}

/// <summary>
/// Raised when a persisted snapshot no longer verifies. Restore must fail loudly
/// here - treating a corrupt record as "not persisted" would rebuild from the
/// wrong state.
/// </summary>
public sealed class SnapshotIntegrityViolationException : Exception
{
    public SnapshotIntegrityViolationException(StateSnapshot snapshot)
        : base($"快照 {snapshot.RoomId}（version {snapshot.Version}）完整性校验失败，不可用于恢复")
    {
    }
}

/// <summary>
/// 存储后端抽象（审计「存储组件：按实际持久化、查询、吞吐和恢复需求选择」）：
/// 后端可换（内存/SQLite/PG/...），快照契约不变。
/// </summary>
public interface ISnapshotBackend
{
    StateSnapshot? TryGet(string roomId);

    void Put(StateSnapshot snapshot);

    bool Delete(string roomId);

    IReadOnlyList<string> ListRoomIds();
}

/// <summary>
/// In-memory reference backend: the honest single-process form. It is not
/// durable across a restart - persistence requires a real backend.
/// </summary>
public sealed class InMemorySnapshotBackend : ISnapshotBackend
{
    private readonly ConcurrentDictionary<string, StateSnapshot> _snapshots = new(StringComparer.Ordinal);

    public StateSnapshot? TryGet(string roomId) =>
        _snapshots.TryGetValue(roomId, out var snapshot) ? snapshot : null;

    public void Put(StateSnapshot snapshot) => _snapshots[snapshot.RoomId] = snapshot;

    public bool Delete(string roomId) => _snapshots.TryRemove(roomId, out _);

    public IReadOnlyList<string> ListRoomIds() =>
        _snapshots.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
}

/// <summary>
/// Snapshot persistence layer with a monotonic version guard and a recompute-on-
/// write integrity rule (audit「持久化快照必须有版本与完整性校验」).
///
/// <list type="bullet">
/// <item>版本单调：version 必须比已存快照大才能覆盖——旧写者的迟到快照绝不覆盖新状态
/// （旧写者隔离的存储半边，同房间同版本重放按幂等 no-op 处理）。</item>
/// <item>完整性：写入时重算校验和，读取时校验；校验不过抛
/// <see cref="SnapshotIntegrityViolationException"/>，而不是当成「未持久化」。</item>
/// <item>后端可插拔：DB 选型按需求决定，不硬编码。</item>
/// </list>
/// 本层只负责持久化半边；状态重建（IGameModule 恢复契约）属 §10 待拍板项。
/// </summary>
public sealed class SnapshotStore
{
    private readonly ISnapshotBackend _backend;

    public SnapshotStore(ISnapshotBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>
    /// Persists a snapshot, recomputing the checksum. Returns the canonical
    /// record. Returns false when the version is not newer than what is already
    /// stored (fail-visible: a stale write is never applied).
    /// </summary>
    public bool TrySave(StateSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (string.IsNullOrWhiteSpace(snapshot.RoomId))
        {
            throw new ArgumentException("room id 不能为空", nameof(snapshot));
        }

        if (snapshot.Version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.Version, "快照版本从 1 开始");
        }

        if (snapshot.SchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.SchemaVersion, "快照 schema 版本从 1 开始");
        }

        if (string.IsNullOrWhiteSpace(snapshot.Payload))
        {
            throw new ArgumentException("payload 不能为空", nameof(snapshot));
        }

        var canonical = snapshot with { Checksum = SnapshotIntegrity.Compute(snapshot.Payload) };
        if (_backend.TryGet(snapshot.RoomId) is { } existing)
        {
            if (canonical.Version <= existing.Version)
            {
                return false;
            }
        }

        _backend.Put(canonical);
        return true;
    }

    /// <summary>
    /// Persists a snapshot and throws on a non-monotonic version. Use when the
    /// caller has already promised the write; <see cref="TrySave"/> is the
    /// fail-visible choice for producers that can retry.
    /// </summary>
    public StateSnapshot Save(StateSnapshot snapshot)
    {
        if (TrySave(snapshot))
        {
            return snapshot with { Checksum = SnapshotIntegrity.Compute(snapshot.Payload) };
        }

        throw new InvalidOperationException(
            $"快照版本 {snapshot.Version} 未超过已存版本，拒绝覆盖（旧写者隔离）");
    }

    /// <summary>
    /// Loads a snapshot, verifying its integrity. Unknown rooms return null; a
    /// corrupt record throws <see cref="SnapshotIntegrityViolationException"/>.
    /// </summary>
    public StateSnapshot? Load(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("room id 不能为空", nameof(roomId));
        }

        var snapshot = _backend.TryGet(roomId.Trim());
        if (snapshot is null || !SnapshotIntegrity.Verify(snapshot))
        {
            if (snapshot is not null)
            {
                throw new SnapshotIntegrityViolationException(snapshot);
            }

            return null;
        }

        return snapshot;
    }

    /// <summary>Deletes a persisted snapshot. False when the room has none.</summary>
    public bool Delete(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("room id 不能为空", nameof(roomId));
        }

        return _backend.Delete(roomId.Trim());
    }

    /// <summary>All rooms with a persisted snapshot, ordered by id.</summary>
    public IReadOnlyList<string> ListRoomIds() => _backend.ListRoomIds();
}
