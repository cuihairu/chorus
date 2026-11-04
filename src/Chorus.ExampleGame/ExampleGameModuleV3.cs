using System.Text.Json;

namespace Chorus.ExampleGame;

/// <summary>
/// The 1.2.0 upgrade of the default factions module (FIX-INPUT-09, the like rule): the
/// 1.1.0 rules are kept — a gift scores its recipient's faction the amount, a boost scores
/// a flat 12 × level — and one rule is added — a like accumulates into its recipient
/// faction's count, and every time that count crosses a whole multiple of the threshold the
/// faction earns one burst of the bonus. The burst is declared as its own effect kind so the
/// triggered behavior is visible evidence, and the round result gains a <c>likes</c> section
/// so the applied total can be reconciled against what the platform accepted. The state
/// records are the shared family's (schema version 1), but the 1.2.0 states carry the
/// <see cref="ExampleGameState.LikeByFaction"/> map the earlier versions leave null — so this
/// version's serialized shape and state hashes differ from theirs, and a room pinned to
/// another package's hash diverges on the first like instead of silently sharing arithmetic.
/// </summary>
public sealed class ExampleGameModuleV3(
    RedesignGameModuleManifest? manifest = null,
    int? likeThreshold = null,
    int? likeBurstBonus = null)
    : IRedesignGameModule
{
    /// <summary>The shipped threshold: a faction bursts once per whole multiple of 20 likes
    /// (the fired count over a round is floor(totalLikes / 20)).</summary>
    public const int LikeThresholdDefault = 20;

    /// <summary>The shipped burst bonus: each threshold crossing adds 50 to the faction's
    /// score (the like rule's triggered积分变化).</summary>
    public const int LikeBurstBonusDefault = 50;

    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public RedesignGameModuleManifest Manifest { get; } = manifest ?? new(
        "example-factions", "1.2.0", 50, 100, RedesignGameModuleManifest.DeterministicReplay);

    /// <summary>The rule's own constants, stated as facts the host or a test can assert
    /// against: the accumulated likes that trigger one burst, and the score it adds. The
    /// constructor parameters are optional — null or a non-positive value falls back to the
    /// shipped constant, so the catalog's package always runs the shipped rule.</summary>
    public int LikeThreshold { get; } = likeThreshold is > 0 ? likeThreshold.Value : LikeThresholdDefault;

    public int LikeBurstBonus { get; } = likeBurstBonus is > 0 ? likeBurstBonus.Value : LikeBurstBonusDefault;

    public ExampleGameState CreateInitialState(GameStartConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(config.Participants);
        if (config.DurationTicks <= 0) throw new ArgumentException("Duration must be positive.");
        if (config.Participants.Count == 0)
            throw new ArgumentException("A round needs participants.");
        var ordered = config.Participants
            .OrderBy(participant => participant.ParticipantId, StringComparer.Ordinal).ToArray();
        if (ordered.Select(participant => participant.ParticipantId).Distinct(StringComparer.Ordinal).Count()
            != ordered.Length)
            throw new ArgumentException("Participants must be distinct.");
        var factions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var participant in ordered)
        {
            if (participant.Faction is not (GameStartConfig.RedFaction or GameStartConfig.BlueFaction))
                throw new ArgumentException("A participant faction must be red or blue.");
            factions[participant.ParticipantId] = participant.Faction;
        }
        return new ExampleGameState(0, config.RandomSeed, config.DurationTicks, config.FailAfterTick,
            factions, new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                [GameStartConfig.RedFaction] = 0,
                [GameStartConfig.BlueFaction] = 0
            }, Finished: false,
            LikeByFaction: new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                [GameStartConfig.RedFaction] = 0,
                [GameStartConfig.BlueFaction] = 0
            });
    }

    /// <summary>Pure: the 1.2.0 rules. Gifts and boosts score exactly as 1.1.0; a like
    /// accumulates its carried count (a missing or non-positive count is one physical click)
    /// into its recipient faction's tally, one <c>like</c> effect per faction per tick merges
    /// the window's likes, and each whole multiple of the threshold crossed earns one
    /// <c>like-burst</c> effect plus the bonus. The seed advances by the family's LCG step,
    /// unchanged — only the like rule uses no seed draw.</summary>
    public TickOutcome ApplyTick(ExampleGameState state, long tick, IReadOnlyList<NormalizedInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inputs);
        if (tick != state.Tick + 1)
            throw new InvalidOperationException($"Ticks advance one at a time ({state.Tick} → {tick}).");
        if (state.Finished) throw new InvalidOperationException("A finished round does not tick.");
        if (state.FailAfterTick > 0 && tick >= state.FailAfterTick)
            throw new InvalidOperationException(
                $"The example module failed deterministically at tick {tick} (test seam).");

        var seed = 1103515245L * state.Seed + 12345 & long.MaxValue; // same LCG step as 1.0.0
        var scores = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (faction, score) in state.ScoreByFaction) scores[faction] = score;
        var likes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (state.LikeByFaction is not null)
            foreach (var (faction, count) in state.LikeByFaction) likes[faction] = count;
        var delta = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var likeDelta = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            using var document = JsonDocument.Parse(input.PayloadJson);
            var root = document.RootElement;
            if (!state.FactionByParticipant.TryGetValue(input.ParticipantId, out var faction))
                continue; // platform events can arrive for participants outside this round
            if (root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "gift")
            {
                var points = GetInt32(root, "amount");
                scores[faction] += points;
                delta[faction] = delta.GetValueOrDefault(faction) + points;
            }
            else if (root.TryGetProperty("kind", out var like) && like.ValueKind == JsonValueKind.String
                && like.GetString() == "like")
            {
                var count = GetInt32(root, "count");
                if (count <= 0) count = 1; // a like without a stated count is one physical click
                likes[faction] = likes.GetValueOrDefault(faction) + count;
                likeDelta[faction] = likeDelta.GetValueOrDefault(faction) + count;
            }
            else if (root.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String && action.GetString() == "boost")
            {
                var points = 12L * GetInt32(root, "level");
                scores[faction] += points;
                delta[faction] = delta.GetValueOrDefault(faction) + points;
            }
            else
            {
                continue; // unknown shapes are ignored deterministically, never crash the round
            }
        }

        // The threshold rule first: each whole multiple of the threshold crossed this tick earns
        // one burst of the bonus, so the score effect's total below is the authoritative score
        // of the tick — gift, boost and burst together, the same invariant the earlier versions
        // state for their gift-and-boost-only scores.
        var burstsByFaction = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (faction, count) in likeDelta)
        {
            var crossed = likes[faction] / LikeThreshold - (likes[faction] - count) / LikeThreshold;
            if (crossed > 0)
            {
                scores[faction] += (long)crossed * LikeBurstBonus;
                burstsByFaction[faction] = crossed;
            }
        }

        var effects = new List<ModuleEffect>();
        foreach (var (faction, points) in delta)
            effects.Add(new ModuleEffect("score",
                JsonSerializer.Serialize(new { faction, delta = points, total = scores[faction] }, Canonical)));
        // The like window merge: one like effect per faction this tick, the summed count, so
        // the client's evidence and the reconciliation read clicks, not callbacks.
        foreach (var (faction, count) in likeDelta)
            effects.Add(new ModuleEffect("like",
                JsonSerializer.Serialize(new { faction, count, total = likes[faction] }, Canonical)));
        // One like-burst effect per firing — the count of these over a round is exactly the
        // configured firing count floor(totalLikes / threshold), visible trigger evidence.
        foreach (var (faction, crossed) in burstsByFaction)
            for (var burst = 0; burst < crossed; burst++)
                effects.Add(new ModuleEffect("like-burst",
                    JsonSerializer.Serialize(new
                    {
                        faction, threshold = LikeThreshold, bonus = LikeBurstBonus,
                        total = scores[faction]
                    }, Canonical)));

        var finished = tick >= state.DurationTicks;
        string? result = null;
        if (finished)
        {
            var ranking = scores
                .Select(entry => new { faction = entry.Key, score = entry.Value })
                .OrderByDescending(entry => entry.score, Comparer<long>.Default)
                .ThenBy(entry => entry.faction, StringComparer.Ordinal).ToArray();
            var applied = likes
                .Select(entry => new { faction = entry.Key, count = entry.Value })
                .OrderBy(entry => entry.faction, StringComparer.Ordinal).ToArray();
            result = JsonSerializer.Serialize(new
            {
                ranking, winner = ranking[0].score == ranking[^1].score
                    ? "draw" : ranking[0].faction,
                likes = applied
            }, Canonical);
        }
        return new TickOutcome(state with
        {
            Tick = tick, Seed = seed, ScoreByFaction = scores, LikeByFaction = likes, Finished = finished
        }, effects, finished, result);
    }

    public byte[] Serialize(ExampleGameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = state.SchemaVersion,
            state.Tick,
            state.Seed,
            state.DurationTicks,
            state.FailAfterTick,
            state.Finished,
            factions = state.FactionByParticipant,
            scores = state.ScoreByFaction,
            likes = state.LikeByFaction ?? new SortedDictionary<string, long>(StringComparer.Ordinal)
        }, Canonical);
    }

    public ExampleGameState Restore(byte[] payload, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (schemaVersion != 1) throw new ArgumentException("Unknown schema version.", nameof(schemaVersion));
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var factions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("factions").EnumerateObject())
            factions[entry.Name] = entry.Value.GetString()!;
        var scores = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("scores").EnumerateObject())
            scores[entry.Name] = entry.Value.GetInt64();
        var likes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (root.TryGetProperty("likes", out var likesElement) && likesElement.ValueKind == JsonValueKind.Object)
            foreach (var entry in likesElement.EnumerateObject())
                likes[entry.Name] = entry.Value.GetInt64();
        return new ExampleGameState(
            root.GetProperty("tick").GetInt64(), root.GetProperty("seed").GetInt64(),
            root.GetProperty("duration_ticks").GetInt64(), root.GetProperty("fail_after_tick").GetInt32(),
            factions, scores, root.GetProperty("finished").GetBoolean(), likes);
    }

    public byte[] ComputeStateHash(ExampleGameState state) =>
        System.Security.Cryptography.SHA256.HashData(Serialize(state));

    public IReadOnlyList<NormalizedInput> OnHostUnavailable() => [];

    /// <summary>FIX-SYNC-08: the pause-timeout rule — freeze the ranking at the scores the
    /// round paused with, carrying the frozen likes map (authoritative-sync.md:54).</summary>
    public TickOutcome FinishFrozen(ExampleGameState state, long tick, string reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (tick != state.Tick) throw new InvalidOperationException(
            $"The frozen tick {tick} does not match the state's tick {state.Tick}.");
        var likes = state.LikeByFaction ??
            new SortedDictionary<string, long>(StringComparer.Ordinal);
        var result = ModuleResult.BuildResult(state, likes)
            ?? throw new InvalidOperationException("The frozen state has no score map to rank.");
        return new TickOutcome(state with { Finished = true }, [], Final: true, result);
    }

    private static int GetInt32(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            return value.GetInt32();
        return 0;
    }
}
