using System.Collections.Concurrent;

namespace Chorus.Data;

/// <summary>One derived statistic: a named metric scoped by id (e.g. room).</summary>
public sealed record StatSample(string Metric, string ScopeId, long Value);

/// <summary>
/// 统计与分析管道（审计 §8.1「普通统计：可以异步计算、延迟更新，部分情况下可丢弃并重建」+ §8.3）：
/// 从 DataCommandOutbox 批量消费命令，按 CommandType 注册的提取器产出派生统计样本。
///
/// <list type="bullet">
/// <item>幂等：同 CommandId 绝不重复计数（§8.3 幂等覆盖全部级别）。</item>
/// <item>可丢弃可重建：计数器与去重台账都是普通统计——<see cref="Reset"/> 是重建入口，
/// 丢失一条只损失一次重算，不是数据；不丢失的级别走 DataCommandOutbox 的
/// RejectedFull 通道，不经过本管道。</item>
/// <item>可观测、不致命：未知 CommandType 与提取器异常跳过并计数（SkippedCount），
/// 统计故障绝不拖垮引擎。</item>
/// <item>有界批处理：DrainOnce 每次最多处理 maxCommands 条，实时循环可控。</item>
/// </list>
/// 聚合的复杂分析（离线/窗口聚合）属后续项；此处是管道消费端的权威语义实现。
/// </summary>
public sealed class AnalyticsPipeline
{
    private sealed class Counter
    {
        public long Value;
    }

    private readonly DataCommandOutbox _source;
    private readonly ConcurrentDictionary<string, Func<DataCommand, IReadOnlyList<StatSample>>> _extractors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Metric, string ScopeId), Counter> _counters = new();
    private readonly ConcurrentDictionary<string, byte> _processedCommands = new(StringComparer.Ordinal);

    public AnalyticsPipeline(DataCommandOutbox source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>Commands skipped (unknown type or extractor failure) - observable, never silent.</summary>
    public long SkippedCount => Interlocked.Read(ref _skippedCount);

    /// <summary>Redelivered commands suppressed by the CommandId ledger.</summary>
    public long DuplicateCount => Interlocked.Read(ref _duplicateCount);

    private long _skippedCount;
    private long _duplicateCount;

    /// <summary>Registers (or replaces) the sample extractor for a command type.</summary>
    public void Register(string commandType, Func<DataCommand, IReadOnlyList<StatSample>> extractor)
    {
        if (string.IsNullOrWhiteSpace(commandType))
        {
            throw new ArgumentException("command type 不能为空", nameof(commandType));
        }

        _extractors[commandType.Trim()] = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    /// <summary>
    /// Drains up to <paramref name="maxCommands"/> commands from the source
    /// outbox into the counters. Returns the number consumed (processed,
    /// duplicated or skipped alike).
    /// </summary>
    public int DrainOnce(int maxCommands = 256)
    {
        if (maxCommands < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCommands));
        }

        var consumed = 0;
        while (consumed < maxCommands && _source.TryDequeue(out var envelope))
        {
            consumed++;
            Apply(envelope!);
        }

        return consumed;
    }

    /// <summary>Current value of a metric scope; zero for unknown metrics.</summary>
    public long Get(string metric, string scopeId) =>
        _counters.TryGetValue((metric, scopeId), out var counter) ? Interlocked.Read(ref counter.Value) : 0;

    /// <summary>All non-zero metric scopes (observability/snapshot).</summary>
    public IReadOnlyList<StatSample> Snapshot() =>
        _counters
            .Select(kv => new StatSample(kv.Key.Metric, kv.Key.ScopeId, Interlocked.Read(ref kv.Value.Value)))
            .OrderBy(s => s.Metric, StringComparer.Ordinal)
            .ThenBy(s => s.ScopeId, StringComparer.Ordinal)
            .Where(s => s.Value != 0)
            .ToArray();

    /// <summary>Clears counters and the CommandId ledger (普通统计 rebuild entry).</summary>
    public void Reset()
    {
        _counters.Clear();
        _processedCommands.Clear();
    }

    private void Apply(DataCommandEnvelope envelope)
    {
        var command = envelope.Command;

        // 幂等锚点：同 CommandId 重放绝不重复计数。
        if (!_processedCommands.TryAdd(command.CommandId, 0))
        {
            Interlocked.Increment(ref _duplicateCount);
            return;
        }

        if (!_extractors.TryGetValue(command.CommandType, out var extractor))
        {
            Interlocked.Increment(ref _skippedCount);
            return;
        }

        IReadOnlyList<StatSample> samples;
        try
        {
            samples = extractor(command);
        }
        catch
        {
            // 提取器故障只影响这一条：跳过并计数，统计绝不拖垮引擎。
            Interlocked.Increment(ref _skippedCount);
            return;
        }

        if (samples is null)
        {
            Interlocked.Increment(ref _skippedCount);
            return;
        }

        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Metric) || string.IsNullOrWhiteSpace(sample.ScopeId))
            {
                continue;
            }

            var counter = _counters.GetOrAdd((sample.Metric, sample.ScopeId), _ => new Counter());
            Interlocked.Add(ref counter.Value, sample.Value);

            // "*" 聚合键：跨 scope 总量（查询便利，不算双计——它有自己的 counter）。
            var total = _counters.GetOrAdd((sample.Metric, "*"), _ => new Counter());
            Interlocked.Add(ref total.Value, sample.Value);
        }
    }
}
