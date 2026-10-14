using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// 玩家资料仓储金测（todo §15 第二项，契约 = 审计 §8.1「玩家资料与进度」）：
/// 必须定义版本、并发写入、幂等和查询一致性——
/// 版本号守卫拒绝旧写覆盖新值（乐观并发），同版本重放是幂等 no-op 而不是错误，
/// 未知玩家 fail-visible，查询返回已提交的最新版本。
/// </summary>
public sealed class PlayerRepositoryTests
{
    private static PlayerRecord NewPlayer(string playerId = "player-7", long version = 1, string? nickname = "小明") =>
        new(playerId, version, new Dictionary<string, string> { ["nickname"] = nickname! });

    [Fact]
    public void TryGet_UnknownPlayer_ReturnsNull()
    {
        var repository = new PlayerRepository();

        Assert.Null(repository.TryGet("ghost"));
    }

    [Fact]
    public void TryCreate_NewPlayer_StoresVersionOne()
    {
        var repository = new PlayerRepository();

        Assert.True(repository.TryCreate(NewPlayer()));

        var stored = repository.TryGet("player-7")!;
        Assert.Equal(1, stored.Version);
        Assert.Equal("小明", stored.Profile["nickname"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void TryCreate_BlankPlayerId_Throws(string? playerId)
    {
        var repository = new PlayerRepository();

        Assert.ThrowsAny<ArgumentException>(() => repository.TryCreate(NewPlayer(playerId!)));
    }

    [Fact]
    public void TryCreate_DuplicatePlayerId_FailsVisibleWithoutOverwrite()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer());

        // 同 id 再创建 = 冲突：false 且旧数据不被覆盖（不隐式 upsert）。
        Assert.False(repository.TryCreate(NewPlayer(nickname: "改名")));
        Assert.Equal("小明", repository.TryGet("player-7")!.Profile["nickname"]);
    }

    [Fact]
    public void TryCreate_FutureVersion_FailsVisible()
    {
        var repository = new PlayerRepository();

        // 创建只能落在 version 1：从未来版本「创建」说明上游漏了第一版，不猜不补。
        Assert.Throws<ArgumentException>(() => repository.TryCreate(NewPlayer(version: 2)));
    }

    [Fact]
    public void TryCreate_SameVersionReplay_IsIdempotentNoOp()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer());

        // 同版本重放 = 幂等 no-op：成功返回 true，状态不变（重复投递不报错也不改数）。
        Assert.True(repository.TryCreate(NewPlayer()));
        Assert.Equal(1, repository.TryGet("player-7")!.Version);
        Assert.Equal(1, repository.Count);
    }

    [Fact]
    public void TryUpdate_AppliesNextVersion()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer());

        var outcome = repository.TryUpdate(NewPlayer(version: 2, nickname: "大明"));

        Assert.True(outcome);
        Assert.Equal(2, repository.TryGet("player-7")!.Version);
        Assert.Equal("大明", repository.TryGet("player-7")!.Profile["nickname"]);
    }

    [Fact]
    public void TryUpdate_UnknownPlayer_FailsVisible()
    {
        var repository = new PlayerRepository();

        Assert.False(repository.TryUpdate(NewPlayer()));
    }

    [Fact]
    public void TryUpdate_SameVersionTwice_IsIdempotentNoOp()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer());

        Assert.True(repository.TryUpdate(NewPlayer(version: 2, nickname: "大明")));
        // 同版本再应用一次 = 幂等 no-op：不报错、不改数、不推进版本。
        Assert.True(repository.TryUpdate(NewPlayer(version: 2, nickname: "大明")));
        Assert.Equal(2, repository.TryGet("player-7")!.Version);
    }

    [Fact]
    public void TryUpdate_StaleVersion_RejectedWithoutOverwrite()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer());
        repository.TryUpdate(NewPlayer(version: 2, nickname: "大明"));

        // 旧版本回写被拒：并发写场景下 last-writer-wins 被版本守卫挡住，状态不被旧值覆盖。
        Assert.False(repository.TryUpdate(NewPlayer(version: 2, nickname: "旧值")));
        Assert.Equal("大明", repository.TryGet("player-7")!.Profile["nickname"]);
    }

    [Fact]
    public void TryUpdate_VersionGap_Rejected()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer());

        // 跳版本（1→3）被拒：版本必须连续，断链说明中间有一版丢失，fail-visible 不猜。
        Assert.False(repository.TryUpdate(NewPlayer(version: 3)));
        Assert.Equal(1, repository.TryGet("player-7")!.Version);
    }

    [Fact]
    public void TryUpdate_NextVersionOutOfOrder_OnlyOneWinsPerSlot()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer());

        // 两个并发写都基于 version 1 提交 version 2：恰好一个成功，无撕裂状态。
        var first = repository.TryUpdate(NewPlayer(version: 2, nickname: "A"));
        var second = repository.TryUpdate(NewPlayer(version: 2, nickname: "B"));

        Assert.True(first);
        Assert.False(second);
        Assert.Equal("A", repository.TryGet("player-7")!.Profile["nickname"]);
    }

    [Fact]
    public void TryUpdate_PartialProfileMerge_KeepsExistingKeys()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer());

        // 资料并发写合并：只改 nickname 不丢其他键（幂等命令允许带完整或部分资料）。
        var partial = new PlayerRecord("player-7", 2, new Dictionary<string, string> { ["level"] = "3" });
        Assert.True(repository.TryUpdate(partial));

        var merged = repository.TryGet("player-7")!;
        Assert.Equal(2, merged.Version);
        Assert.Equal("小明", merged.Profile["nickname"]);
        Assert.Equal("3", merged.Profile["level"]);
    }

    [Fact]
    public void Snapshot_ReturnsPlayersSortedById()
    {
        var repository = new PlayerRepository();
        repository.TryCreate(NewPlayer("player-c"));
        repository.TryCreate(NewPlayer("player-a"));

        Assert.Equal(new[] { "player-a", "player-c" }, repository.Snapshot().Select(p => p.PlayerId));
    }
}
