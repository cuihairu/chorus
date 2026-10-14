using System.Collections.Concurrent;

namespace Chorus.Data;

/// <summary>
/// 游戏记录（审计 §8.1「游戏记录」）：一局的权威结果/结算事实，追加式不可改。
/// <c>RecordId</c> is the idempotency key; <c>Sequence</c> is producer-supplied
/// business order (the store never assumes it is monotonic on arrival).
/// </summary>
public sealed record GameRecord
{
    public GameRecord(string recordId, string roomId, long? sequence, string? matchId, string payload)
    {
        if (string.IsNullOrWhiteSpace(recordId))
        {
            throw new ArgumentException("record id 不能为空", nameof(RecordId));
        }

        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("room id 不能为空", nameof(RoomId));
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("payload 不能为空", nameof(Payload));
        }

        RecordId = recordId;
        RoomId = roomId;
        Sequence = sequence;
        MatchId = matchId;
        Payload = payload;
    }

    /// <summary>Producer-assigned unique id; the idempotency key.</summary>
    public string RecordId { get; init; }

    /// <summary>Originating room - every record must be attributable.</summary>
    public string RoomId { get; init; }

    /// <summary>Producer business order; may be null for unordered facts.</summary>
    public long? Sequence { get; init; }

    /// <summary>Optional match scope.</summary>
    public string? MatchId { get; init; }

    /// <summary>Record payload (JSON).</summary>
    public string Payload { get; init; }
}

/// <summary>
/// Append-only game record store (audit §8.1「游戏记录：关键比赛结果应有可靠持久化边界」).
///
/// <list type="bullet">
/// <item>不可改：同一 RecordId 重放同内容是幂等 no-op（true，不重复入账）；
/// 同 RecordId 不同内容或不同 RoomId 是冲突（false），已存记录永不被覆盖。</item>
/// <item>顺序由生产者负责：仓储不校验 sequence 单调性，迟到与重放是常态。</item>
/// <item>查询一致性：同一房间的记录总按 sequence 升序、游标严格大于返回；
/// 无 sequence 的记录按稳定追加顺序排在有 sequence 的记录之后。</item>
/// </list>
/// 可靠持久化边界由交接层（DataCommandOutbox + 业务流水）提供，此处是持久化前的
/// 权威投影；生产接线属后续项。
/// </summary>
public sealed class GameRecordRepository
{
    private sealed record Entry(long InsertedAt, GameRecord Record);

    private readonly ConcurrentDictionary<string, Entry> _records = new(StringComparer.Ordinal);
    private long _nextInsertion;

    /// <summary>Number of stored records.</summary>
    public int Count => _records.Count;

    /// <summary>Returns the stored record, or null for an unknown RecordId.</summary>
    public GameRecord? TryGet(string? recordId) =>
        !string.IsNullOrWhiteSpace(recordId) && _records.TryGetValue(recordId, out var entry)
            ? entry.Record
            : null;

    /// <summary>
    /// Appends a record. A same-content replay is an idempotent no-op (true,
    /// the record is not counted twice); a differing payload, room, sequence or
    /// match for the same RecordId is a conflict (false).
    /// </summary>
    public bool TryAppend(GameRecord record)
    {
        if (_records.TryGetValue(record.RecordId, out var existing))
        {
            return SameContent(existing.Record, record);
        }

        if (!_records.TryAdd(record.RecordId, new Entry(Interlocked.Increment(ref _nextInsertion), record)))
        {
            // Lost the race; a record with this id is already stored - compare content.
            return _records.TryGetValue(record.RecordId, out var winner) && SameContent(winner.Record, record);
        }

        return true;
    }

    /// <summary>
    /// Records of a room ordered by sequence ascending (stable insertion order
    /// for records without a sequence). <paramref name="sequenceAfter"/> is an
    /// exclusive cursor; <paramref name="limit"/> null means unlimited.
    /// </summary>
    public IReadOnlyList<GameRecord> ListForRoom(
        string? roomId,
        long? sequenceAfter = null,
        int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("room id 不能为空", nameof(roomId));
        }

        var records = _records.Values
            .Where(e => e.Record.RoomId == roomId
                && (sequenceAfter is null || e.Record.Sequence > sequenceAfter))
            .OrderBy(e => e.Record.Sequence ?? long.MaxValue)
            .ThenBy(e => e.InsertedAt)
            .Select(e => e.Record)
            .ToList();

        return limit is null || records.Count <= limit.Value ? records : records[..limit.Value];
    }

    /// <summary>Number of records of a room (observability).</summary>
    public int CountForRoom(string? roomId) => ListForRoom(roomId).Count;

    private static bool SameContent(GameRecord stored, GameRecord incoming) =>
        stored.RoomId == incoming.RoomId
        && stored.MatchId == incoming.MatchId
        && stored.Sequence == incoming.Sequence
        && stored.Payload == incoming.Payload;
}
