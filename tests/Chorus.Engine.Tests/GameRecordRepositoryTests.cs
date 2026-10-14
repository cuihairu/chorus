using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// 游戏记录仓储金测（todo §15 第二项，契约 = 审计 §8.1「游戏记录」）：
/// 追加式不可改（同一 RecordId 不同内容 fail-visible，绝不覆盖历史记录），
/// 同内容重放是幂等 no-op（重投递不重复入账），
/// 查询总有一个确定顺序（sequence 后稳定排序），提供可靠持久化边界内的确定性读取。
/// </summary>
public sealed class GameRecordRepositoryTests
{
    private static GameRecord NewRecord(
        string recordId = "rec-1",
        string roomId = "room-1",
        long? sequence = null,
        string? matchId = "match-9",
        string payload = """{"result":"win"}""") =>
        new(recordId, roomId, sequence, matchId, payload);

    [Fact]
    public void TryAppend_NewRecord_Succeeds()
    {
        var repository = new GameRecordRepository();

        Assert.True(repository.TryAppend(NewRecord()));
        Assert.Equal(1, repository.Count);
        Assert.Equal(1, repository.CountForRoom("room-1"));
    }

    [Theory]
    [InlineData("RecordId", null, "r", "{}")]
    [InlineData("RoomId", "rec-1", " ", "{}")]
    [InlineData("Payload", "rec-1", "r", " ")]
    public void TryAppend_BlankField_Throws(string field, string? recordId, string roomId, string payload)
    {
        var repository = new GameRecordRepository();

        var args = Assert.ThrowsAny<ArgumentException>(() =>
            repository.TryAppend(new GameRecord(recordId ?? "", roomId, null, null, payload)));
        Assert.Equal(field, args.ParamName);
    }

    [Fact]
    public void TryAppend_DuplicateSameContent_IsIdempotentNoOp()
    {
        var repository = new GameRecordRepository();
        repository.TryAppend(NewRecord());

        // 同内容重放 = 幂等 no-op：返回成功、不重复入账、不改变已存记录。
        Assert.True(repository.TryAppend(NewRecord()));
        Assert.Equal(1, repository.Count);
    }

    [Fact]
    public void TryAppend_DuplicateDifferentContent_FailsVisible()
    {
        var repository = new GameRecordRepository();
        repository.TryAppend(NewRecord());

        // 同 RecordId 不同内容 = 冲突：历史记录不可改写，false 且已存记录不被覆盖。
        Assert.False(repository.TryAppend(NewRecord(payload: """{"result":"loss"}""")));
        Assert.Equal("""{"result":"win"}""", repository.TryGet("rec-1")!.Payload);
        Assert.Equal(1, repository.Count);
    }

    [Fact]
    public void TryAppend_DuplicateDifferentRoom_FailsVisible()
    {
        var repository = new GameRecordRepository();
        repository.TryAppend(NewRecord());

        Assert.False(repository.TryAppend(NewRecord(roomId: "room-2")));
        Assert.Equal("room-1", repository.TryGet("rec-1")!.RoomId);
    }

    [Fact]
    public void TryGet_UnknownRecord_ReturnsNull()
    {
        var repository = new GameRecordRepository();

        Assert.Null(repository.TryGet("ghost"));
    }

    [Fact]
    public void ListForRoom_OrdersBySequenceRegardlessOfAppendOrder()
    {
        var repository = new GameRecordRepository();
        // 乱序到达（重试/并发写入场景）：追加顺序不等于业务顺序。
        repository.TryAppend(NewRecord("rec-3", sequence: 3));
        repository.TryAppend(NewRecord("rec-1", sequence: 1));
        repository.TryAppend(NewRecord("rec-2", sequence: 2));

        Assert.Equal(new long?[] { 1, 2, 3 }, repository.ListForRoom("room-1").Select(r => r.Sequence));
    }

    [Fact]
    public void ListForRoom_FiltersByRoomId()
    {
        var repository = new GameRecordRepository();
        repository.TryAppend(NewRecord("rec-a", roomId: "room-1"));
        repository.TryAppend(NewRecord("rec-b", roomId: "room-2"));
        repository.TryAppend(NewRecord("rec-c", roomId: "room-1"));

        Assert.Equal(new[] { "rec-a", "rec-c" }, repository.ListForRoom("room-1").Select(r => r.RecordId));
        Assert.Equal(new[] { "rec-b" }, repository.ListForRoom("room-2").Select(r => r.RecordId));
        Assert.Empty(repository.ListForRoom("room-3"));
    }

    [Fact]
    public void ListForRoom_AfterSequence_PaginatesWithoutOverlap()
    {
        var repository = new GameRecordRepository();
        for (var seq = 1L; seq <= 5; seq++)
        {
            repository.TryAppend(NewRecord($"rec-{seq}", sequence: seq));
        }

        var firstPage = repository.ListForRoom("room-1", sequenceAfter: null, limit: 2);
        var secondPage = repository.ListForRoom("room-1", sequenceAfter: firstPage[^1].Sequence, limit: 2);

        // 游标分页：两页不重叠、不遗漏，边界严格大于（同一 sequence 不会重复出现）。
        Assert.Equal(new long?[] { 1, 2 }, firstPage.Select(r => r.Sequence).ToArray());
        Assert.Equal(new long?[] { 3, 4 }, secondPage.Select(r => r.Sequence).ToArray());
        Assert.Single(repository.ListForRoom("room-1", sequenceAfter: 4, limit: 10));
    }

    [Fact]
    public void ListForRoom_NullSequence_KeepsStableAppendOrder()
    {
        var repository = new GameRecordRepository();
        repository.TryAppend(NewRecord("rec-a", sequence: null));
        repository.TryAppend(NewRecord("rec-b", sequence: null));

        // 无 sequence 的记录按稳定追加顺序输出，查询结果确定不抖动。
        Assert.Equal(new[] { "rec-a", "rec-b" }, repository.ListForRoom("room-1").Select(r => r.RecordId));
    }

    [Fact]
    public void TryAppend_UnknownSequenceRange_AllowsAnyValue()
    {
        var repository = new GameRecordRepository();

        // 顺序语义由生产者负责：仓储不校验 sequence 单调性（迟到/重放是常态）。
        Assert.True(repository.TryAppend(NewRecord("rec-new", sequence: 100)));
        Assert.True(repository.TryAppend(NewRecord("rec-older", sequence: 50)));
        Assert.Equal(new long?[] { 50, 100 }, repository.ListForRoom("room-1").Select(r => r.Sequence));
    }
}
