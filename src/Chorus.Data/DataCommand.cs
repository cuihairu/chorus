namespace Chorus.Data;

/// <summary>
/// Data plane reliability tier (audit §8.1). The tier is part of the command
/// contract, not a delivery detail: it decides what the pipeline is allowed to
/// do when the Data Worker is overloaded or a delivery fails.
/// </summary>
public enum DataReliabilityLevel
{
    /// <summary>
    /// 普通统计：可异步、可丢弃、可重建。Derived numbers (counters, aggregates)
    /// whose source of truth is the live match - losing one costs a recompute,
    /// not data.
    /// </summary>
    Statistical = 0,

    /// <summary>
    /// 玩家资料：需要版本号 + 幂等 + 并发写合并。Last-writer-wins with a version
    /// guard is acceptable; silently losing a write is not.
    /// </summary>
    PlayerProfile = 1,

    /// <summary>
    /// 游戏记录：需要按审计恢复要求持久化。Must survive a Data Worker restart and
    /// be replayable after recovery - it is the audit trail the other tiers are
    /// derived from.
    /// </summary>
    GameRecord = 2,

    /// <summary>
    /// 关键经济：需要业务流水 + 幂等键 + 事务约束 + 失败补偿。A business ledger:
    /// the producer must not be told "accepted" before the command is durably
    /// recorded somewhere that cannot lose it (§8.1 - a promise must never rest
    /// on a lossy in-memory queue alone).
    /// </summary>
    CriticalEconomy = 3,
}

/// <summary>
/// Per-tier delivery semantics (audit §8.1/§8.3). Every tier must have an
/// explicit answer for all five questions - an unstated tier would be a silent
/// policy gap, so the switch is exhaustive and throws on undefined values.
/// </summary>
public static class DataReliabilityPolicy
{
    /// <summary>反压时可丢弃：仅普通统计可丢（可重建），其余级别一律 fail-visible。</summary>
    public static bool MayDropOnBackpressure(DataReliabilityLevel level) => level switch
    {
        DataReliabilityLevel.Statistical => true,
        DataReliabilityLevel.PlayerProfile => false,
        DataReliabilityLevel.GameRecord => false,
        DataReliabilityLevel.CriticalEconomy => false,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "未定义的数据可靠性级别"),
    };

    /// <summary>必须落盘：除普通统计外的全部级别。</summary>
    public static bool MustPersist(DataReliabilityLevel level) => level switch
    {
        DataReliabilityLevel.Statistical => false,
        DataReliabilityLevel.PlayerProfile => true,
        DataReliabilityLevel.GameRecord => true,
        DataReliabilityLevel.CriticalEconomy => true,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "未定义的数据可靠性级别"),
    };

    /// <summary>需要失败补偿：仅关键经济（业务流水要求可对账、可补偿）。</summary>
    public static bool RequiresFailureCompensation(DataReliabilityLevel level) => level switch
    {
        DataReliabilityLevel.CriticalEconomy => true,
        DataReliabilityLevel.PlayerProfile => false,
        DataReliabilityLevel.GameRecord => false,
        DataReliabilityLevel.Statistical => false,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "未定义的数据可靠性级别"),
    };

    /// <summary>
    /// 需要幂等：覆盖全部级别（§8.3「数据命令必须有幂等语义」）。Even a rebuildable
    /// statistic must not double-count when it is redelivered.
    /// </summary>
    public static bool RequiresIdempotency(DataReliabilityLevel level) => level switch
    {
        DataReliabilityLevel.Statistical => true,
        DataReliabilityLevel.PlayerProfile => true,
        DataReliabilityLevel.GameRecord => true,
        DataReliabilityLevel.CriticalEconomy => true,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "未定义的数据可靠性级别"),
    };

    /// <summary>
    /// 需要保序：仅对有业务顺序含义的级别（游戏记录、关键经济）。
    /// 重试必须考虑顺序与重复执行（§8.3）——玩家资料与普通统计无顺序语义。
    /// </summary>
    public static bool RequiresOrdering(DataReliabilityLevel level) => level switch
    {
        DataReliabilityLevel.GameRecord => true,
        DataReliabilityLevel.CriticalEconomy => true,
        DataReliabilityLevel.PlayerProfile => false,
        DataReliabilityLevel.Statistical => false,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "未定义的数据可靠性级别"),
    };
}

/// <summary>
/// A data-plane write request (audit §8.2). The shape follows the audit
/// contract; <c>Level</c> has no default, so a critical write can never be
/// silently constructed without a tier (and therefore without its delivery
/// guarantees).
/// </summary>
/// <param name="CommandId">Producer-assigned unique id; the idempotency key.</param>
/// <param name="CommandType">Dotted command name, e.g. <c>player.score.submit</c>.</param>
/// <param name="RoomId">Originating room - every command must be attributable.</param>
/// <param name="Level">Reliability tier driving delivery guarantees.</param>
/// <param name="Payload">Command payload (JSON).</param>
/// <param name="MatchId">Optional match scope.</param>
/// <param name="PlayerId">Optional player scope.</param>
/// <param name="SourceEventId">Optional audience event that caused the write.</param>
/// <param name="Sequence">Optional producer sequence for ordering.</param>
/// <param name="SchemaVersion">Payload schema version; defaults to the current version (1).</param>
public sealed record DataCommand(
    string CommandId,
    string CommandType,
    string RoomId,
    DataReliabilityLevel Level,
    string Payload,
    string? MatchId = null,
    string? PlayerId = null,
    string? SourceEventId = null,
    long? Sequence = null,
    int SchemaVersion = 1);

/// <summary>
/// Contract validation, fail-visible (audit §8.3「关键数据不能在失败时静默丢弃」).
/// Returns the offending field names so a caller can log/assert on them; an
/// empty result means the command is well formed.
/// </summary>
public static class DataCommands
{
    /// <summary>Field name reported when the command itself is null.</summary>
    public const string NullCommandField = "command";

    /// <summary>
    /// Validates a command against the §8.2 contract. Returns one entry per
    /// violated rule: <c>command</c> for a null command, otherwise the field
    /// name (<c>CommandId</c>, <c>CommandType</c>, <c>RoomId</c>,
    /// <c>Level</c>, <c>SchemaVersion</c>, <c>Payload</c>).
    /// </summary>
    public static IReadOnlyList<string> Validate(DataCommand? command)
    {
        if (command is null)
        {
            return [NullCommandField];
        }

        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            errors.Add(nameof(DataCommand.CommandId));
        }

        if (!Enum.IsDefined(typeof(DataReliabilityLevel), command.Level))
        {
            // 未定义级别 = 策略空洞：投递后只能按猜测的默认策略处理，必须在校验层拦下。
            errors.Add(nameof(DataCommand.Level));
        }

        if (string.IsNullOrWhiteSpace(command.CommandType))
        {
            errors.Add(nameof(DataCommand.CommandType));
        }

        if (string.IsNullOrWhiteSpace(command.RoomId))
        {
            errors.Add(nameof(DataCommand.RoomId));
        }

        if (!Enum.IsDefined(command.Level))
        {
            // An untyped tier would fall through to a guessed policy - refuse it.
            errors.Add(nameof(DataCommand.Level));
        }

        if (command.SchemaVersion <= 0)
        {
            // Version 0 would decode as "unknown contract" - fail loudly instead.
            errors.Add(nameof(DataCommand.SchemaVersion));
        }

        if (string.IsNullOrWhiteSpace(command.Payload))
        {
            errors.Add(nameof(DataCommand.Payload));
        }

        return errors;
    }

    /// <summary>True when the command satisfies the contract.</summary>
    public static bool IsValid(DataCommand? command) => Validate(command).Count == 0;
}
