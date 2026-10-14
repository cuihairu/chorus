using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// 统计与分析管道金测（todo §15 末项，契约 = 审计 §8.1「普通统计」+ §8.3）：
/// 派生统计可丢弃可重建（Reset 即重建入口）、幂等覆盖全部级别（同 CommandId 绝不重复计）、
/// 未知命令类型/提取器异常跳过并计数可观测（统计故障不拖垮引擎）、批处理有界。
/// </summary>
public sealed class AnalyticsPipelineTests
{
    private static DataCommand NewCommand(string id, string type = "audience.comment", string roomId = "room-1") =>
        new(
            CommandId: id,
            CommandType: type,
            RoomId: roomId,
            Level: DataReliabilityLevel.Statistical,
            Payload: """{"n":1}""");

    private static AnalyticsPipeline NewPipeline(DataCommandOutbox? source = null)
    {
        var pipeline = new AnalyticsPipeline(source ?? new DataCommandOutbox(64));
        pipeline.Register("audience.comment", c => [new StatSample("comments.total", c.RoomId, 1)]);
        return pipeline;
    }

    [Fact]
    public void Drain_ProcessesEachCommandIntoSamples()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1"), 1_000);
        outbox.Enqueue(NewCommand("cmd-2", roomId: "room-2"), 1_001);
        var pipeline = NewPipeline(outbox);

        var processed = pipeline.DrainOnce();

        Assert.Equal(2, processed);
        Assert.Equal(2, pipeline.Get("comments.total", "room-1") + pipeline.Get("comments.total", "room-2"));
        Assert.Equal(2, pipeline.Get("comments.total", "*"));
    }

    [Fact]
    public void Get_UnknownMetric_IsZero()
    {
        var pipeline = NewPipeline();

        Assert.Equal(0, pipeline.Get("ghost.metric", "room-1"));
    }

    [Fact]
    public void Get_AccumulatesAcrossCommands()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1"), 1_000);
        outbox.Enqueue(NewCommand("cmd-2"), 1_001);
        outbox.Enqueue(NewCommand("cmd-3"), 1_002);
        var pipeline = NewPipeline(outbox);

        pipeline.DrainOnce();

        Assert.Equal(3, pipeline.Get("comments.total", "room-1"));
    }

    [Fact]
    public void Drain_DuplicateCommandId_ProcessedOnce()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1"), 1_000);
        var pipeline = NewPipeline(outbox);

        pipeline.DrainOnce();

        // 同 CommandId 重投递（outbox 下游重放）不重复计数（§8.3 幂等覆盖全部级别）。
        outbox.Enqueue(NewCommand("cmd-1"), 2_000);
        pipeline.DrainOnce();

        Assert.Equal(1, pipeline.Get("comments.total", "room-1"));
        Assert.Equal(1, pipeline.DuplicateCount);
    }

    [Fact]
    public void Drain_UnknownCommandType_SkippedAndCounted()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1", type: "unknown.type"), 1_000);
        var pipeline = NewPipeline(outbox);

        var processed = pipeline.DrainOnce();

        // 未知类型跳过但计数可观测——不崩、不静默蒸发。
        Assert.Equal(1, processed);
        Assert.Equal(0, pipeline.Get("comments.total", "room-1"));
        Assert.Equal(1, pipeline.SkippedCount);
    }

    [Fact]
    public void Drain_ThrowingExtractor_SkippedAndCounted()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1", type: "boom"), 1_000);
        var pipeline = NewPipeline(outbox);
        pipeline.Register("boom", _ => throw new InvalidOperationException("bad payload"));

        pipeline.DrainOnce();

        // 提取器异常只影响该命令：统计故障不拖垮引擎，跳过计数可观测。
        Assert.Equal(1, pipeline.SkippedCount);
        Assert.Equal(0, pipeline.Get("comments.total", "room-1"));
    }

    [Fact]
    public void Drain_EmptyOutbox_ReturnsZero()
    {
        var pipeline = NewPipeline();

        Assert.Equal(0, pipeline.DrainOnce());
    }

    [Fact]
    public void Drain_RespectsMaxCommands()
    {
        var outbox = new DataCommandOutbox(64);
        for (var i = 0; i < 10; i++)
        {
            outbox.Enqueue(NewCommand($"cmd-{i}"), 1_000 + i);
        }

        var pipeline = NewPipeline(outbox);

        Assert.Equal(3, pipeline.DrainOnce(maxCommands: 3));
        Assert.Equal(3, pipeline.Get("comments.total", "room-1"));

        pipeline.DrainOnce(maxCommands: 100);
        Assert.Equal(10, pipeline.Get("comments.total", "room-1"));
    }

    [Fact]
    public void Scopes_AreIsolatedByRoom()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1", roomId: "room-1"), 1_000);
        outbox.Enqueue(NewCommand("cmd-2", roomId: "room-2"), 1_001);
        var pipeline = NewPipeline(outbox);

        pipeline.DrainOnce();

        Assert.Equal(1, pipeline.Get("comments.total", "room-1"));
        Assert.Equal(1, pipeline.Get("comments.total", "room-2"));
    }

    [Fact]
    public void Reset_ClearsCountersForRebuild()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1"), 1_000);
        var pipeline = NewPipeline(outbox);
        pipeline.DrainOnce();

        // 普通统计可丢弃并重建（§8.1）：Reset 即重建入口，清除后从 0 重新聚合。
        pipeline.Reset();

        Assert.Equal(0, pipeline.Get("comments.total", "room-1"));
        Assert.Equal(0, pipeline.Get("comments.total", "*"));
    }

    [Fact]
    public void Register_DuplicateCommandType_Replaces()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1"), 1_000);
        var pipeline = NewPipeline(outbox);
        pipeline.Register("audience.comment", _ => [new StatSample("comments.other", "room-1", 5)]);

        pipeline.DrainOnce();

        Assert.Equal(0, pipeline.Get("comments.total", "room-1"));
        Assert.Equal(5, pipeline.Get("comments.other", "room-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Register_BlankCommandType_Throws(string blank)
    {
        var pipeline = NewPipeline();

        Assert.ThrowsAny<ArgumentException>(() =>
            pipeline.Register(blank, _ => [new StatSample("m", "r", 1)]));
    }

    [Fact]
    public void Snapshot_ExposesAllMetrics()
    {
        var outbox = new DataCommandOutbox(64);
        outbox.Enqueue(NewCommand("cmd-1", roomId: "room-1"), 1_000);
        outbox.Enqueue(NewCommand("cmd-2", type: "audience.gift", roomId: "room-1"), 1_001);
        var pipeline = NewPipeline(outbox);
        pipeline.Register("audience.gift", c => [new StatSample("gifts.total", c.RoomId, 1)]);

        pipeline.DrainOnce();

        // 每个 scope 一条 + 每个指标一条 "*" 聚合 = 4 条；明细与聚合都在快照里。
        Assert.Equal(4, pipeline.Snapshot().Count);
        Assert.Contains(pipeline.Snapshot(), s => s.Metric == "gifts.total" && s.ScopeId == "room-1" && s.Value == 1);
        Assert.Contains(pipeline.Snapshot(), s => s.Metric == "comments.total" && s.ScopeId == "room-1" && s.Value == 1);
        Assert.Contains(pipeline.Snapshot(), s => s.Metric == "gifts.total" && s.ScopeId == "*" && s.Value == 1);
    }
}
