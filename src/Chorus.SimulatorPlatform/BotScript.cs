using Tomlyn;

namespace Chorus.SimulatorPlatform;

/// <summary>The audience event kinds a bot script can schedule. Names double as the
/// TOML <c>kind</c> values and as the ingress payload's <c>kind</c> string (the
/// simulator mapper parses enum names case-insensitively).</summary>
public enum ScriptEventKind
{
    Comment = 1,
    Like = 2,
    Gift = 3,
    SuperChat = 6,
    GuardBuy = 7,
    Enter = 4,
}

/// <summary>One steady stretch of the timeline: per-kind rates in effect from
/// <paramref name="fromSeconds"/> until the next segment (or the script end).</summary>
public sealed record ScriptSegment(
    double FromSeconds, double ToSeconds,
    double CommentPerSecond, double LikePerSecond, double GiftPerSecond,
    double SuperChatPerSecond, double GuardBuyPerSecond, double EnterPerSecond,
    string GiftId, int GiftValue, string? Text);

/// <summary>One peak burst: exactly <paramref name="count"/> events of one kind,
/// fired at <paramref name="atSeconds"/> — the script's exact-count instrument.</summary>
public sealed record ScriptPulse(
    double AtSeconds, ScriptEventKind Kind, int Count,
    string? GiftId, int? GiftValue, string? Text);

/// <summary>One scheduled audience event: everything the ingress poster needs.</summary>
public sealed record ScheduledBotEvent(
    double OffsetSeconds, ScriptEventKind Kind, string UserId,
    string? Text, string? GiftId, int GiftValue);

/// <summary>What one script run will send, counted up front — the reconciliation
/// expectations derive from these totals plus the poster's own outcome counts.</summary>
public sealed record BotScriptTotals(
    IReadOnlyDictionary<ScriptEventKind, long> ByKind, long GiftValueSum);

/// <summary>
/// A bots script: one simulator live session's audience-event timeline (BOTS-1).
/// The TOML shape — <c>[script]</c> (name, duration, optional seed), <c>[streamer]</c>
/// (openId, liveKey), ordered <c>[[segment]]</c> steady-rate windows, and exact-count
/// <c>[[pulse]]</c> bursts — compiles to a fully scheduled event list up front, so a run's
/// totals are known before the first byte goes out and the reconciliation library can hold
/// the run to them. Gifts are unit-count (a batch is several events, which is also how the
/// dedup chain gets exercised); the demo module scores gifts by value, so the pulse gifts
/// carry the exact faction-score expectations.
/// </summary>
public sealed record BotScript(
    string Name, double DurationSeconds, string OpenId, string LiveKey, int? Seed,
    IReadOnlyList<ScriptSegment> Segments, IReadOnlyList<ScriptPulse> Pulses,
    IReadOnlyList<ScheduledBotEvent> Schedule, BotScriptTotals Totals)
{
    public static BotScript Parse(string toml, int viewerCount = 8) =>
        Parse(toml, viewerCount, out _);

    /// <summary>Parses and compiles a script. Collects every defect before throwing, so a
    /// bad script names all its problems at once.</summary>
    public static BotScript Parse(string toml, int viewerCount, out IReadOnlyList<string> errors)
    {
        var defects = new List<string>();
        errors = defects;
        var document = Toml.Parse(toml);
        foreach (var diagnostic in document.Diagnostics)
            defects.Add($"toml: {diagnostic}");
        Tomlyn.Model.TomlTable? model = null;
        try
        {
            if (document.TryToModel<Tomlyn.Model.TomlTable>(out var parsed, out _))
                model = parsed;
        }
        catch (Exception)
        {
            // Tomlyn's transform throws instead of answering diagnostics on some malformed
            // documents; the syntax diagnostics above already say what was wrong.
        }
        if (model is null)
            defects.Add("toml: the document did not parse to a table");
        if (defects.Count > 0)
            throw new ArgumentException($"Invalid bot script:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", defects)}");
        var root = model;

        var script = Table(root, "script", defects, required: true);
        var streamer = Table(root, "streamer", defects, required: true);
        var name = String(script, "name", defects);
        var duration = Double(script, "duration", defects, minimum: 0.1);
        var openId = String(streamer, "openId", defects);
        var liveKey = String(streamer, "liveKey", defects);
        int? seed = (int?)Integer(script, "seed", defects);

        var segments = new List<ScriptSegment>();
        foreach (var entry in Tables(root, "segment", defects, required: true))
        {
            var from = Double(entry, "from", defects, minimum: 0);
            var to = Double(entry, "to", defects, minimum: 0);
            if (from is { } f && to is { } t && f >= t)
                defects.Add($"segment: from ({f}) must be before to ({t})");
            var giftId = String(entry, "giftId", defects);
            var giftValue = Integer(entry, "giftValue", defects);
            if (Double(entry, "giftPerSecond", defects) is > 0 && giftId is null)
                defects.Add("segment: a gift rate needs giftId");
            segments.Add(new ScriptSegment(
                from ?? 0, to ?? 0,
                Double(entry, "commentPerSecond", defects) ?? 0,
                Double(entry, "likePerSecond", defects) ?? 0,
                Double(entry, "giftPerSecond", defects) ?? 0,
                Double(entry, "superChatPerSecond", defects) ?? 0,
                Double(entry, "guardBuyPerSecond", defects) ?? 0,
                Double(entry, "enterPerSecond", defects) ?? 0,
                giftId ?? "", (int?)giftValue ?? 0,
                String(entry, "text", defects)));
        }
        for (var i = 1; i < segments.Count; i++)
        {
            if (segments[i].FromSeconds < segments[i - 1].FromSeconds)
                defects.Add($"segment[{i}]: from ({segments[i].FromSeconds}) breaks the ascending order");
        }

        var pulses = new List<ScriptPulse>();
        foreach (var entry in Tables(root, "pulse", defects, required: false))
        {
            var at = Double(entry, "at", defects, minimum: 0);
            var kindName = String(entry, "kind", defects);
            ScriptEventKind kind = default;
            if (kindName is null || !Enum.TryParse(kindName, ignoreCase: true, out kind))
                defects.Add($"pulse: kind '{kindName ?? "(missing)"}' is not a script event kind");
            if (duration is { } d && at is { } a && a >= d)
                defects.Add($"pulse: at ({a}) is at or past the script duration ({d})");
            var count = Integer(entry, "count", defects, minimum: 1);
            var pulseGiftId = String(entry, "giftId", defects);
            var pulseGiftValue = Integer(entry, "giftValue", defects);
            if (kind == ScriptEventKind.Gift && (pulseGiftId is null || pulseGiftValue is null))
                defects.Add("pulse: a Gift pulse needs giftId and giftValue");
            pulses.Add(new ScriptPulse(at ?? 0, kind, (int?)count ?? 0,
                pulseGiftId, (int?)pulseGiftValue, String(entry, "text", defects)));
        }

        if (defects.Count > 0)
            throw new ArgumentException(
                $"Invalid bot script:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", defects)}");

        var schedule = CompileSchedule(seed, segments, pulses, Math.Max(1, viewerCount), duration!.Value, defects);
        var byKind = schedule.GroupBy(e => e.Kind)
            .ToDictionary(group => group.Key, group => (long)group.Count());
        var totals = new BotScriptTotals(byKind,
            schedule.Where(e => e.Kind == ScriptEventKind.Gift).Sum(e => e.GiftValue));
        return new BotScript(name ?? "bots-script", duration!.Value, openId ?? "bot-streamer",
            liveKey ?? openId ?? "bot-live", seed, segments, pulses, schedule, totals);
    }

    /// <summary>Turns segments and pulses into the absolute schedule. Steady streams get
    /// seeded jitter (first event inside the first interval, then fixed intervals — the
    /// long-run rate still converges to the target, the same absolute-schedule rule the
    /// load tool's pacer uses); pulses fire exactly their count, 1 ms apart so the order
    /// survives the sort. Viewer identities round-robin at schedule time.</summary>
    private static List<ScheduledBotEvent> CompileSchedule(int? seed,
        IReadOnlyList<ScriptSegment> segments, IReadOnlyList<ScriptPulse> pulses,
        int viewerCount, double durationSeconds, List<string> defects)
    {
        var random = seed is { } s ? new Random(s) : Random.Shared;
        var schedule = new List<ScheduledBotEvent>();
        var viewer = 0;
        var rate = (double value, string field) =>
        {
            if (value < 0 || !double.IsFinite(value))
                defects.Add($"{field}: rate must be a finite non-negative number (got {value})");
            return Math.Max(0, value);
        };
        foreach (var segment in segments)
        {
            var from = segment.FromSeconds * 1000;
            var to = Math.Min(segment.ToSeconds, durationSeconds) * 1000;
            foreach (var (kind, perSecond) in new (ScriptEventKind, double)[]
            {
                (ScriptEventKind.Comment, rate(segment.CommentPerSecond, "segment: commentPerSecond")),
                (ScriptEventKind.Like, rate(segment.LikePerSecond, "segment: likePerSecond")),
                (ScriptEventKind.Gift, rate(segment.GiftPerSecond, "segment: giftPerSecond")),
                (ScriptEventKind.SuperChat, rate(segment.SuperChatPerSecond, "segment: superChatPerSecond")),
                (ScriptEventKind.GuardBuy, rate(segment.GuardBuyPerSecond, "segment: guardBuyPerSecond")),
                (ScriptEventKind.Enter, rate(segment.EnterPerSecond, "segment: enterPerSecond")),
            })
            {
                if (perSecond <= 0) continue;
                var intervalMs = 1000.0 / perSecond;
                for (var at = from + intervalMs * random.NextDouble(); at < to; at += intervalMs)
                    schedule.Add(new ScheduledBotEvent(at / 1000.0, kind,
                        $"bot-v{viewer++ % viewerCount}",
                        segment.Text, segment.GiftId, segment.GiftValue));
            }
        }
        foreach (var pulse in pulses)
        {
            for (var i = 0; i < pulse.Count; i++)
                schedule.Add(new ScheduledBotEvent(pulse.AtSeconds + i * 0.001, pulse.Kind,
                    $"bot-v{viewer++ % viewerCount}", pulse.Text, pulse.GiftId, pulse.GiftValue ?? 0));
        }
        if (defects.Count > 0)
            throw new ArgumentException(
                $"Invalid bot script:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", defects)}");
        schedule.Sort((left, right) => left.OffsetSeconds.CompareTo(right.OffsetSeconds));
        return schedule;
    }

    // ---- small typed readers over Tomlyn's object model --------------------------------

    private static IDictionary<string, object?>? Table(IDictionary<string, object?> root,
        string key, List<string> defects, bool required)
    {
        if (!root.TryGetValue(key, out var value))
        {
            if (required) defects.Add($"missing [{key}] table");
            return null;
        }
        return value as IDictionary<string, object?>
            ?? Adding<IDictionary<string, object?>>(defects, $"[{key}] must be a table");
    }

    private static List<IDictionary<string, object?>> Tables(IDictionary<string, object?> root,
        string key, List<string> defects, bool required)
    {
        if (!root.TryGetValue(key, out var value))
        {
            if (required) defects.Add($"missing [[{key}]] entries");
            return [];
        }
        // [[key]] maps to a table array; an inline array of tables maps to a plain array.
        var entries = value switch
        {
            Tomlyn.Model.TomlTableArray tableArray => (System.Collections.IEnumerable)tableArray,
            Tomlyn.Model.TomlArray plainArray => plainArray,
            _ => null,
        };
        if (entries is null)
        {
            Adding<object?>(defects, $"[{key}] must be an array of tables");
            return [];
        }
        var tables = new List<IDictionary<string, object?>>();
        foreach (var entry in entries)
            if (entry is IDictionary<string, object?> table) tables.Add(table);
            else Adding<object?>(defects, $"[{key}] entries must be tables");
        return tables;
    }

    private static string? String(IDictionary<string, object?>? table, string key, List<string> defects)
    {
        if (table is null || !table.TryGetValue(key, out var value)) return null;
        return value as string ?? Adding<string>(defects, $"{key} must be a string");
    }

    private static double? Double(IDictionary<string, object?>? table, string key, List<string> defects,
        double? minimum = null)
    {
        if (table is null || !table.TryGetValue(key, out var value)) return null;
        var number = value switch
        {
            long integer => (double)integer,
            double real => real,
            _ => Adding<double>(defects, $"{key} must be a number"),
        };
        if (minimum is { } floor && number < floor)
            defects.Add($"{key} must be at least {floor} (got {number})");
        return number;
    }

    private static long? Integer(IDictionary<string, object?>? table, string key, List<string> defects,
        long? minimum = null)
    {
        if (table is null || !table.TryGetValue(key, out var value)) return null;
        var integer = value switch
        {
            long whole => whole,
            double real => Adding<long>(defects, $"{key} must be an integer (got {real})"),
            _ => Adding<long>(defects, $"{key} must be an integer"),
        };
        if (minimum is { } floor && integer < floor)
            defects.Add($"{key} must be at least {floor} (got {integer})");
        return integer;
    }

    private static T Adding<T>(List<string> defects, string message)
    {
        defects.Add(message);
        return default!;
    }
}
