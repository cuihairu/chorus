using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-06, the pagination cursor's pure contract (developer-capabilities.md「分页实现可返回
/// 有期限的快照版本/游标；快照失效显式错误」): a cursor is opaque in transit but decodable here,
/// and it is valid only while it names the same board, the same write version and an unexpired
/// snapshot lifetime. A board change or an expired snapshot is a stale read — never a
/// cross-version seemingly-contiguous page.
/// </summary>
public sealed class RedesignRank06CursorUnitTests
{
    private static LeaderboardCursorProto MakeValid(DateTimeOffset deadline) => new()
    {
        TenantId = "tenant-a", GameId = "game-1", BoardId = "board-1",
        WriteVersion = "2026-01-01T00:00:00+00:00", DeadlineUtcMs = deadline.ToUnixTimeMilliseconds(),
        LastScore = 8, LastSubjectId = "b"
    };

    [Fact]
    public void AValidCursorRoundtripsThroughEncodeAndParse()
    {
        var cursor = MakeValid(DateTimeOffset.UtcNow.AddSeconds(60));
        var token = LeaderboardCursor.Encode(cursor);
        Assert.True(LeaderboardCursor.TryParse(token, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(cursor.TenantId, parsed!.TenantId);
        Assert.Equal(cursor.GameId, parsed.GameId);
        Assert.Equal(cursor.BoardId, parsed.BoardId);
        Assert.Equal(cursor.WriteVersion, parsed.WriteVersion);
        Assert.Equal(cursor.DeadlineUtcMs, parsed.DeadlineUtcMs);
        Assert.Equal(cursor.LastScore, parsed.LastScore);
        Assert.Equal(cursor.LastSubjectId, parsed.LastSubjectId);
    }

    [Fact]
    public void AMalformedTokenIsNotACursor()
    {
        Assert.False(LeaderboardCursor.TryParse("not-base64", out var malformed));
        Assert.Null(malformed);
        Assert.False(LeaderboardCursor.TryParse("", out var empty));
        Assert.Null(empty);
        // A token that is valid base64 but not a cursor message is refused too — never silently
        // treated as a first page (that would stitch a new snapshot over an old walk).
        Assert.False(LeaderboardCursor.TryParse(Convert.ToBase64String([1, 2, 3]), out var junk));
        Assert.Null(junk);
    }

    [Fact]
    public void AnExpiredSnapshotIsInvalid()
    {
        var cursor = MakeValid(DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(LeaderboardCursor.IsValid(cursor, "tenant-a", "game-1", "board-1",
            cursor.WriteVersion, DateTimeOffset.UtcNow, out var reason));
        Assert.Contains("expired", reason);
    }

    [Fact]
    public void AChangedBoardVersionIsInvalid()
    {
        var cursor = MakeValid(DateTimeOffset.UtcNow.AddSeconds(60));
        Assert.False(LeaderboardCursor.IsValid(cursor, "tenant-a", "game-1", "board-1",
            "2026-01-02T00:00:00+00:00", DateTimeOffset.UtcNow, out var reason));
        Assert.Contains("changed", reason);
    }

    [Fact]
    public void ACursorForAnotherBoardIsInvalid()
    {
        var cursor = MakeValid(DateTimeOffset.UtcNow.AddSeconds(60));
        Assert.False(LeaderboardCursor.IsValid(cursor, "tenant-b", "game-1", "board-1",
            cursor.WriteVersion, DateTimeOffset.UtcNow, out var reason));
        Assert.Contains("another board", reason);
    }

    [Fact]
    public void AValidCursorPasses()
    {
        var cursor = MakeValid(DateTimeOffset.UtcNow.AddSeconds(60));
        Assert.True(LeaderboardCursor.IsValid(cursor, "tenant-a", "game-1", "board-1",
            cursor.WriteVersion, DateTimeOffset.UtcNow, out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void MakeBindsTheKeysetAndKeepsTheWalkDeadline()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var last = new RankEntry(RankSubject.Player, "z", 4);
        var cursor = LeaderboardCursor.Make("tenant-a", "game-1", "board-1",
            "v1", deadline, last);
        Assert.Equal(4, cursor.LastScore);
        Assert.Equal("z", cursor.LastSubjectId);
        Assert.Equal(deadline.ToUnixTimeMilliseconds(), cursor.DeadlineUtcMs);
        Assert.Equal("v1", cursor.WriteVersion);
        // An empty last entry is never a cursor — a cursor must resume after a real entry.
        Assert.Throws<ArgumentException>(() => LeaderboardCursor.Make(
            "tenant-a", "game-1", "board-1", "v1", deadline, new RankEntry(RankSubject.Player, "", 0)));
    }
}
