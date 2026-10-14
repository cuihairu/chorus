using Chorus.Adapters.Platforms.MiniGame;

namespace Chorus.Engine.Tests;

/// <summary>
/// 小游戏排行榜投影金测（todo §17 排行榜适配）：能力门控（未核实平台不投影，
/// 不编造平台能力）、top-N 截断、空参与者 fail-visible、行序由调用方权威
/// （投影不重排）。
/// </summary>
public sealed class MiniGameLeaderboardProjectorTests
{
    private static MiniGameLeaderboardProjector NewProjector()
        => new(MiniGamePlatformCapabilities.Douyin);

    private static IReadOnlyList<MiniGameLeaderboardRow> Rows(params (int Rank, string Id, long Score)[] rows)
        => rows.Select(r => new MiniGameLeaderboardRow(r.Rank, r.Id, r.Score)).ToArray();

    [Fact]
    public void Project_VerifiedPlatform_ReturnsRowsInGivenOrder()
    {
        var projector = NewProjector();
        var source = Rows((1, "p-1", 100), (2, "p-2", 90), (3, "p-3", 80));

        var projection = projector.Project("douyin", source);

        Assert.NotNull(projection);
        Assert.Equal("douyin", projection!.Platform);
        Assert.Equal(MiniGameCapability.FriendRankData, projection.TargetCapability);
        Assert.Equal(3, projection.Rows.Count);
        Assert.Equal("p-1", projection.Rows[0].ParticipantId);
        Assert.Equal(100, projection.Rows[0].Score);
        Assert.Equal(3, projection.Rows[2].Rank);
    }

    [Fact]
    public void Project_TopTruncates()
    {
        var projector = NewProjector();
        var source = Rows((1, "p-1", 100), (2, "p-2", 90), (3, "p-3", 80));

        var projection = projector.Project("douyin", source, top: 2);

        Assert.NotNull(projection);
        Assert.Equal(2, projection!.Rows.Count);
        Assert.Equal("p-2", projection.Rows[1].ParticipantId);
    }

    [Fact]
    public void Project_EmptySource_ReturnsEmptyProjection()
    {
        var projector = NewProjector();

        var projection = projector.Project("douyin", Array.Empty<MiniGameLeaderboardRow>());

        Assert.NotNull(projection);
        Assert.Empty(projection!.Rows);
    }

    [Fact]
    public void Project_UnknownPlatform_ReturnsNull()
    {
        Assert.Null(NewProjector().Project("ghost", Rows((1, "p-1", 100))));
    }

    [Fact]
    public void Project_UnverifiedCapability_ReturnsNull()
    {
        // 快手小游戏无公开文档：不假设其有好友排行能力。
        var projector = new MiniGameLeaderboardProjector(MiniGamePlatformCapabilities.Kuaishou);

        Assert.Null(projector.Project("kuaishou", Rows((1, "p-1", 100))));
    }

    [Fact]
    public void Project_DouyinRealtimeCapability_ReturnsNull()
    {
        // 抖音小游戏实时通道未找到公开资料(待核)：不得当作可用能力投影。
        var projector = NewProjector();

        Assert.Null(projector.Project(
            "douyin", Rows((1, "p-1", 100)), targetCapability: MiniGameCapability.RealtimeWebSocket));
    }

    [Fact]
    public void Project_DouyinOpenDataDomain_IsSupported()
    {
        var projector = NewProjector();

        var projection = projector.Project(
            "douyin", Rows((1, "p-1", 100)), targetCapability: MiniGameCapability.OpenDataDomain);

        Assert.NotNull(projection);
        Assert.Equal(MiniGameCapability.OpenDataDomain, projection!.TargetCapability);
    }

    [Fact]
    public void Project_BlankParticipantId_Throws()
    {
        var projector = NewProjector();

        Assert.Throws<ArgumentException>(() => projector.Project("douyin", Rows((1, " ", 100))));
    }

    [Fact]
    public void Project_NonPositiveTop_Throws()
    {
        var projector = NewProjector();

        Assert.Throws<ArgumentOutOfRangeException>(() => projector.Project("douyin", Rows((1, "p-1", 100)), top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => projector.Project("douyin", Rows((1, "p-1", 100)), top: -1));
    }

    [Fact]
    public void Project_NullSource_Throws()
        => Assert.Throws<ArgumentNullException>(() => NewProjector().Project("douyin", null!));

    [Fact]
    public void Project_NullCapabilities_Throws()
        => Assert.Throws<ArgumentNullException>(() => new MiniGameLeaderboardProjector(null!));
}
