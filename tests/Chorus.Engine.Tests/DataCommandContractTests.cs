using Chorus.Data;

namespace Chorus.Engine.Tests;

/// <summary>
/// DataCommand 契约与可靠性分级金测（todo §15 首项，契约 = 总体架构审计 §8.1/§8.2/§8.3）：
/// 字段形状按审计建议契约；Level 必填（漏设不静默降级）；分级语义逐条对齐 §8.1
/// （仅普通统计可丢弃并重建、经济数据需失败补偿、幂等语义覆盖全部级别）；
/// 校验 fail-visible（§8.3 关键数据不能静默丢弃）。
/// </summary>
public sealed class DataCommandContractTests
{
    private static DataCommand NewCommand(DataReliabilityLevel level = DataReliabilityLevel.Statistical) =>
        new(
            CommandId: "cmd-1",
            CommandType: "player.score.submit",
            RoomId: "room-1",
            Level: level,
            MatchId: "match-9",
            PlayerId: "player-7",
            SourceEventId: "evt-42",
            Sequence: 13,
            SchemaVersion: 1,
            Payload: """{"score":100}""");

    [Fact]
    public void Contract_RoundTripsAllFields()
    {
        var command = NewCommand(DataReliabilityLevel.CriticalEconomy);

        Assert.Equal("cmd-1", command.CommandId);
        Assert.Equal("player.score.submit", command.CommandType);
        Assert.Equal("room-1", command.RoomId);
        Assert.Equal(DataReliabilityLevel.CriticalEconomy, command.Level);
        Assert.Equal("match-9", command.MatchId);
        Assert.Equal("player-7", command.PlayerId);
        Assert.Equal("evt-42", command.SourceEventId);
        Assert.Equal(13, command.Sequence);
        Assert.Equal(1, command.SchemaVersion);
        Assert.Equal("""{"score":100}""", command.Payload);
    }

    [Fact]
    public void Validate_WellFormedCommand_HasNoErrors()
    {
        Assert.Empty(DataCommands.Validate(NewCommand()));
    }

    [Fact]
    public void Validate_RequiredField_EachBlankIsReported()
    {
        Assert.Contains("CommandId", DataCommands.Validate(new DataCommand(
            CommandId: " ", CommandType: "t", RoomId: "r", Level: DataReliabilityLevel.Statistical, Payload: "{}")));
        Assert.Contains("CommandType", DataCommands.Validate(new DataCommand(
            CommandId: "c", CommandType: " ", RoomId: "r", Level: DataReliabilityLevel.Statistical, Payload: "{}")));
        Assert.Contains("RoomId", DataCommands.Validate(new DataCommand(
            CommandId: "c", CommandType: "t", RoomId: "", Level: DataReliabilityLevel.Statistical, Payload: "{}")));
        Assert.Contains("Payload", DataCommands.Validate(new DataCommand(
            CommandId: "c", CommandType: "t", RoomId: "r", Level: DataReliabilityLevel.Statistical, Payload: " ")));
    }

    [Fact]
    public void Validate_NullCommand_ReportsNull()
    {
        Assert.Contains("command", DataCommands.Validate(null!));
    }

    [Fact]
    public void Validate_UndefinedLevel_IsReported()
    {
        // 未定义分级 = 策略空洞：必须在校验层拦下，不许按默认策略静默投递。
        Assert.Contains("Level", DataCommands.Validate(NewCommand((DataReliabilityLevel)99)));
    }

    [Fact]
    public void Level_IsRequired_ByCompile()
    {
        // Level 是无默认值的构造参数：漏设会在编译期失败，避免关键数据被静默降级成可丢弃级别。
        var command = NewCommand(DataReliabilityLevel.GameRecord);

        Assert.True(command.Level != DataReliabilityLevel.Statistical);
    }

    [Fact]
    public void Policy_UndefinedLevel_FailsVisible()
    {
        // 每个策略入口都对未定义级别 fail-closed，不返回猜测的默认值。
        var undefined = (DataReliabilityLevel)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => DataReliabilityPolicy.MayDropOnBackpressure(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataReliabilityPolicy.MustPersist(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataReliabilityPolicy.RequiresFailureCompensation(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataReliabilityPolicy.RequiresIdempotency(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataReliabilityPolicy.RequiresOrdering(undefined));
    }

    [Fact]
    public void Policy_MayDropOnBackpressure_OnlyStatistical()
    {
        // §8.1：普通统计可丢弃并重建；其余级别不可。
        Assert.True(DataReliabilityPolicy.MayDropOnBackpressure(DataReliabilityLevel.Statistical));
        Assert.False(DataReliabilityPolicy.MayDropOnBackpressure(DataReliabilityLevel.PlayerProfile));
        Assert.False(DataReliabilityPolicy.MayDropOnBackpressure(DataReliabilityLevel.GameRecord));
        Assert.False(DataReliabilityPolicy.MayDropOnBackpressure(DataReliabilityLevel.CriticalEconomy));
    }

    [Fact]
    public void Policy_MustPersist_AllExceptStatistical()
    {
        Assert.False(DataReliabilityPolicy.MustPersist(DataReliabilityLevel.Statistical));
        Assert.True(DataReliabilityPolicy.MustPersist(DataReliabilityLevel.PlayerProfile));
        Assert.True(DataReliabilityPolicy.MustPersist(DataReliabilityLevel.GameRecord));
        Assert.True(DataReliabilityPolicy.MustPersist(DataReliabilityLevel.CriticalEconomy));
    }

    [Fact]
    public void Policy_RequiresFailureCompensation_OnlyCriticalEconomy()
    {
        Assert.False(DataReliabilityPolicy.RequiresFailureCompensation(DataReliabilityLevel.Statistical));
        Assert.False(DataReliabilityPolicy.RequiresFailureCompensation(DataReliabilityLevel.PlayerProfile));
        Assert.False(DataReliabilityPolicy.RequiresFailureCompensation(DataReliabilityLevel.GameRecord));
        Assert.True(DataReliabilityPolicy.RequiresFailureCompensation(DataReliabilityLevel.CriticalEconomy));
    }

    [Fact]
    public void Policy_RequiresIdempotency_AllLevels()
    {
        // §8.3：数据命令必须有幂等语义（覆盖全部级别）。
        foreach (var level in Enum.GetValues<DataReliabilityLevel>())
        {
            Assert.True(DataReliabilityPolicy.RequiresIdempotency(level), $"{level} 未要求幂等");
        }
    }

    [Fact]
    public void Policy_RequiresOrdering_RecordAndEconomy()
    {
        // §8.3：重试必须考虑顺序——有业务顺序含义的级别才要求保序。
        Assert.True(DataReliabilityPolicy.RequiresOrdering(DataReliabilityLevel.GameRecord));
        Assert.True(DataReliabilityPolicy.RequiresOrdering(DataReliabilityLevel.CriticalEconomy));
        Assert.False(DataReliabilityPolicy.RequiresOrdering(DataReliabilityLevel.PlayerProfile));
        Assert.False(DataReliabilityPolicy.RequiresOrdering(DataReliabilityLevel.Statistical));
    }

    [Fact]
    public void Policy_CoversEveryLevel_WithoutGaps()
    {
        // 每个级别都必须有明确的四类语义，不能出现未定义级别。
        foreach (var level in Enum.GetValues<DataReliabilityLevel>())
        {
            _ = DataReliabilityPolicy.MayDropOnBackpressure(level);
            _ = DataReliabilityPolicy.MustPersist(level);
            _ = DataReliabilityPolicy.RequiresFailureCompensation(level);
            _ = DataReliabilityPolicy.RequiresOrdering(level);

            // 语义互斥检查：可丢弃级别不应同时要求失败补偿或持久化。
            if (DataReliabilityPolicy.MayDropOnBackpressure(level))
            {
                Assert.False(DataReliabilityPolicy.MustPersist(level));
                Assert.False(DataReliabilityPolicy.RequiresFailureCompensation(level));
            }
        }
    }
}
