using System.Security.Cryptography;
using System.Text.Json;

namespace Chorus.ExampleGame;

/// <summary>
/// A game module manifest (architecture 8.1): the tick cadence and checkpoint policy are
/// module facts, fixed by the module, not by the host. DeterministicReplay is the default
/// checkpoint mode; a module that cannot guarantee determinism must declare FullCheckpoint —
/// the host then checkpoints every committed tick (FIX-MODULE-03) and recovers by restoring
/// the head checkpoint directly, never by replay: <see cref="SnapshotEveryTicks"/> is the
/// cadence only a DeterministicReplay module runs at.
/// </summary>
public sealed record RedesignGameModuleManifest(
    string Name, string ModuleVersion, int TickMs, int SnapshotEveryTicks, string CheckpointMode)
{
    public const string DeterministicReplay = "DeterministicReplay";
    public const string FullCheckpoint = "FullCheckpoint";

    /// <summary>The shipped cadence: 20 Hz ticks, a full snapshot every 100 ticks.</summary>
    public static RedesignGameModuleManifest Default { get; } =
        new("example-factions", "1.0.0", 50, 100, DeterministicReplay);

    /// <summary>A faster test variant of the same module (25 ms ticks).</summary>
    public static RedesignGameModuleManifest FastTest { get; } = Default with { TickMs = 25 };
}

/// <summary>A participant of the example round: one of exactly two factions.</summary>
public sealed record GameParticipant(string ParticipantId, string Faction);

/// <summary>
/// Start configuration for one round. Everything the initial state derives from is in here —
/// the random seed included — so the same configuration always rebuilds the same state.
/// </summary>
/// <param name="FailAfterTick">
/// Deterministic fault seam for isolation evidence (AT13): when set, the module throws once
/// the tick reaches this value. A test variant of the module, never a host behavior.
/// </param>
public sealed record GameStartConfig(
    long DurationTicks, long RandomSeed, IReadOnlyList<GameParticipant> Participants,
    int FailAfterTick = 0)
{
    public const string RedFaction = "red";
    public const string BlueFaction = "blue";
}

/// <summary>
/// The module state: integers, sorted maps and the RNG seed — no clocks, no unordered
/// collections, no floating point. Every tick copies forward (write-on-apply), so the
/// pre-tick state stays intact for recovery while a candidate is being built.
/// </summary>
public sealed record ExampleGameState(
    long Tick,
    long Seed,
    long DurationTicks,
    int FailAfterTick,
    IReadOnlyDictionary<string, string> FactionByParticipant,
    IReadOnlyDictionary<string, long> ScoreByFaction,
    bool Finished)
{
    public int SchemaVersion => 1;
}

/// <summary>A declarative persistent effect declared by a tick (architecture 8.4).</summary>
public sealed record ModuleEffect(string Kind, string PayloadJson);

/// <summary>The result of one pure tick application: the next state, its effects and, at the
/// final tick, the round result document.</summary>
public sealed record TickOutcome(
    ExampleGameState State, IReadOnlyList<ModuleEffect> Effects, bool Final, string? ResultJson);

/// <summary>
/// One normalized input delivered to the module, already attributed to a participant and
/// ordered by the routing sequence. The module never sees platform metadata or clocks.
/// </summary>
public sealed record NormalizedInput(string ParticipantId, string PayloadJson);

/// <summary>
/// The example gameplay module (architecture 8.4): a trusted server-side logic package with
/// deterministic ApplyTick, Serialize/Restore and a state hash. Two factions score points
/// from gifts and boost commands; the final tick freezes the ranking. All arithmetic is
/// integer, all collections sorted, all randomness drawn from the carried seed.
/// </summary>
public sealed class ExampleGameModule(RedesignGameModuleManifest? manifest = null)
    : IRedesignGameModule
{
    public const string ScoreEffect = "score";

    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public RedesignGameModuleManifest Manifest { get; } = manifest ?? RedesignGameModuleManifest.Default;

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

    /// <summary>
    /// Pure: consumes the ordered inputs and the current state, returns the next state with
    /// its effects. The RNG advances exactly once per tick, from the carried seed.
    /// </summary>
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

        var seed = 1103515245L * state.Seed + 12345 & long.MaxValue; // deterministic LCG step
        var scores = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (faction, score) in state.ScoreByFaction) scores[faction] = score;
        var delta = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            using var document = JsonDocument.Parse(input.PayloadJson);
            var root = document.RootElement;
            if (!state.FactionByParticipant.TryGetValue(input.ParticipantId, out var faction))
                continue; // platform events can arrive for participants outside this round
            long points;
            if (root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "gift")
            {
                points = GetInt32(root, "amount");
            }
            else if (root.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String && action.GetString() == "boost")
            {
                points = 10L * GetInt32(root, "level") + seed % 5;
            }
            else
            {
                continue; // unknown shapes are ignored deterministically, never crash the round
            }
            scores[faction] += points;
            delta[faction] = delta.GetValueOrDefault(faction) + points;
        }

        var effects = new List<ModuleEffect>();
        foreach (var (faction, points) in delta)
            effects.Add(new ModuleEffect(ScoreEffect,
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
            Tick = tick, Seed = seed, ScoreByFaction = scores, Finished = finished
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

    public byte[] ComputeStateHash(ExampleGameState state) => SHA256.HashData(Serialize(state));

    /// <summary>
    /// Control-input seam (architecture 8.2): when the host becomes unavailable the module
    /// must not invent reality from fresh reads. Recovery supplies the persisted control
    /// inputs; until then there are none.
    /// </summary>
    public IReadOnlyList<NormalizedInput> OnHostUnavailable() => [];

    private static int GetInt32(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            return value.GetInt32();
        return 0;
    }
}
