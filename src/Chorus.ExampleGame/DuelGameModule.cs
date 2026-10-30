using System.Text.Json;

namespace Chorus.ExampleGame;

/// <summary>
/// The second shipped module (FIX-MODULE-02's loading evidence): a duel variant of the
/// example family. Same state records and result document shape, different rules — the
/// audience's gifts are noise here (a duel is won by the fighters' own boosts, flat
/// 10 × level, no seed modulation), so the same round inputs produce a different score
/// chain, state hash and result under this package than under <c>pkg-1</c>. That
/// divergence is what proves a room was loaded through the package it named, not
/// through the default module.
/// </summary>
public sealed class DuelGameModule(RedesignGameModuleManifest? manifest = null)
    : IRedesignGameModule
{
    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public RedesignGameModuleManifest Manifest { get; } = manifest ?? new(
        "example-duel", "0.9.0", 25, 100, RedesignGameModuleManifest.DeterministicReplay);

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
            }, Finished: false);
    }

    /// <summary>Pure: a duel scores only its fighters' boosts — gifts are audience noise —
    /// and never draws from the seed, so the score chain depends on inputs alone.</summary>
    public TickOutcome ApplyTick(ExampleGameState state, long tick, IReadOnlyList<NormalizedInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inputs);
        if (tick != state.Tick + 1)
            throw new InvalidOperationException($"Ticks advance one at a time ({state.Tick} → {tick}).");
        if (state.Finished) throw new InvalidOperationException("A finished round does not tick.");
        if (state.FailAfterTick > 0 && tick >= state.FailAfterTick)
            throw new InvalidOperationException(
                $"The duel module failed deterministically at tick {tick} (test seam).");

        var scores = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (faction, score) in state.ScoreByFaction) scores[faction] = score;
        var delta = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            using var document = JsonDocument.Parse(input.PayloadJson);
            var root = document.RootElement;
            if (!state.FactionByParticipant.TryGetValue(input.ParticipantId, out var faction))
                continue; // platform events can arrive for participants outside this round
            if (root.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String && action.GetString() == "boost")
            {
                var points = 10L * GetInt32(root, "level");
                scores[faction] += points;
                delta[faction] = delta.GetValueOrDefault(faction) + points;
            }
            // everything else — gifts included — is noise a duel ignores deterministically
        }

        var effects = new List<ModuleEffect>();
        foreach (var (faction, points) in delta)
            effects.Add(new ModuleEffect("score",
                JsonSerializer.Serialize(new { faction, delta = points, total = scores[faction] }, Canonical)));

        var finished = tick >= state.DurationTicks;
        string? result = null;
        if (finished)
        {
            var ranking = scores
                .Select(pair => new { faction = pair.Key, score = pair.Value })
                .OrderByDescending(entry => entry.score, Comparer<long>.Default)
                .ThenBy(entry => entry.faction, StringComparer.Ordinal).ToArray();
            result = JsonSerializer.Serialize(new
            {
                ranking, winner = ranking[0].score == ranking[^1].score
                    ? "draw" : ranking[0].faction
            }, Canonical);
        }
        return new TickOutcome(state with
        {
            Tick = tick, ScoreByFaction = scores, Finished = finished
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
            scores = state.ScoreByFaction
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
        return new ExampleGameState(
            root.GetProperty("tick").GetInt64(), root.GetProperty("seed").GetInt64(),
            root.GetProperty("duration_ticks").GetInt64(), root.GetProperty("fail_after_tick").GetInt32(),
            factions, scores, root.GetProperty("finished").GetBoolean());
    }

    public byte[] ComputeStateHash(ExampleGameState state) =>
        System.Security.Cryptography.SHA256.HashData(Serialize(state));

    public IReadOnlyList<NormalizedInput> OnHostUnavailable() => [];

    private static int GetInt32(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            return value.GetInt32();
        return 0;
    }
}
