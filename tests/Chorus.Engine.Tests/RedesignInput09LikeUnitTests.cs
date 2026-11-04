using System.Text.Json;
using Chorus.ExampleGame;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-09, the module half: the 1.2.0 like rule. A like accumulates into its recipient
/// faction's count, one like effect per faction per tick merges the window's likes, and each
/// whole multiple of the configured threshold crossed earns one burst of the bonus — the
/// fired count over a round is floor(totalLikes / threshold). The report reads clicks (the
/// carried count), never the callback count; the result's likes section reconciles the
/// applied total against what was accepted; and the 1.2.0 state's serialized shape diverges
/// from the 1.1.0 one, so loading the wrong version is detectable by replay.
/// </summary>
public sealed class RedesignInput09LikeUnitTests
{
    private const string Host = "host";
    private const string Guest = "guest";
    private const string Red = GameStartConfig.RedFaction;
    private const string Blue = GameStartConfig.BlueFaction;

    private static readonly GameStartConfig Config = new(DurationTicks: 3, RandomSeed: 777,
        Participants:
        [
            new GameParticipant(Host, Red),
            new GameParticipant(Guest, Blue)
        ]);

    private static ExampleGameModuleV3 Module(int threshold = 20, int bonus = 50) => new(null, threshold, bonus);

    private static NormalizedInput Like(string participant, int count) =>
        new(participant, JsonSerializer.Serialize(new { kind = "like", count }), FromClient: false);

    private static NormalizedInput Gift(string participant, int amount) =>
        new(participant, JsonSerializer.Serialize(new { kind = "gift", amount }), FromClient: false);

    private static NormalizedInput Boost(string participant, int level) =>
        new(participant, JsonSerializer.Serialize(new { action = "boost", level }), FromClient: false);

    [Fact]
    public void ALikeAccumulatesItsFactionCountAndMergesTheWindowPerTick()
    {
        var module = Module();
        var state = module.CreateInitialState(Config);

        // Three likes in one tick's window, all for the host (red): one merged effect stating
        // three clicks, the tally reads three, blue untouched, no bonus below the threshold.
        var outcome = module.ApplyTick(state, 1,
            [Like(Host, 1), Like(Host, 1), Like(Host, 1)]);

        Assert.Equal(3L, outcome.State.LikeByFaction![Red]);
        Assert.Equal(0L, outcome.State.LikeByFaction[Blue]);
        var like = Assert.Single(outcome.Effects, effect => effect.Kind == "like");
        using (var document = JsonDocument.Parse(like.PayloadJson))
        {
            Assert.Equal(Red, document.RootElement.GetProperty("faction").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("count").GetInt64());
            Assert.Equal(3, document.RootElement.GetProperty("total").GetInt64());
        }
        Assert.Empty(outcome.Effects.Where(effect => effect.Kind == "like-burst"));
        Assert.Empty(outcome.Effects.Where(effect => effect.Kind == "score"));
    }

    [Fact]
    public void ALikeBatchCountsClicksNotCallbacks()
    {
        var module = Module();
        var state = module.CreateInitialState(Config);

        // One callback carrying five clicks (the reducer's merged batch): the report reads
        // five clicks, not one callback — one effect stating the merged five.
        var outcome = module.ApplyTick(state, 1, [Like(Host, 5)]);

        Assert.Equal(5L, outcome.State.LikeByFaction![Red]);
        var like = Assert.Single(outcome.Effects, effect => effect.Kind == "like");
        using var document = JsonDocument.Parse(like.PayloadJson);
        Assert.Equal(5, document.RootElement.GetProperty("count").GetInt64());
    }

    [Fact]
    public void TheThresholdBurstsFollowTheConfiguredCount()
    {
        // A non-shipped threshold and bonus: the rule's own constants must drive the firing
        // count, not a hardcoded 20 — 7 likes at threshold 3 fire floor(7/3) = 2 bursts.
        var module = Module(threshold: 3, bonus: 10);
        var state = module.CreateInitialState(Config);

        var first = module.ApplyTick(state, 1, [Like(Host, 4), Like(Host, 3)]);
        var bursts = first.Effects.Where(effect => effect.Kind == "like-burst").ToArray();
        Assert.Equal(2, bursts.Length);
        Assert.Equal(20L, first.State.ScoreByFaction[Red]);
        using (var document = JsonDocument.Parse(bursts[0].PayloadJson))
        {
            Assert.Equal(Red, document.RootElement.GetProperty("faction").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("threshold").GetInt32());
            Assert.Equal(10, document.RootElement.GetProperty("bonus").GetInt64());
        }

        // Two more likes: total 9, floor(9/3) = 3 — one more burst this tick, three cumulative.
        var second = module.ApplyTick(first.State, 2, [Like(Host, 2)]);
        Assert.Single(second.Effects, effect => effect.Kind == "like-burst");
        Assert.Equal(30L, second.State.ScoreByFaction[Red]);
    }

    [Fact]
    public void TheLikeTallySurvivesSerializeAndRestoreAndDivergesFromTheEarlierVersion()
    {
        var module = Module();
        var state = module.CreateInitialState(Config);
        var liked = module.ApplyTick(state, 1, [Like(Host, 4)]).State;

        var restored = module.Restore(module.Serialize(liked), 1);
        Assert.Equal(4L, restored.LikeByFaction![Red]);
        Assert.Equal(0L, restored.LikeByFaction[Blue]);

        // Two states with identical visible scores but different like tallies hash differently,
        // and the 1.2.0 shape differs from the 1.1.0 one (which omits likes entirely): loading
        // the wrong version is detectable by replay, not by trust.
        var moreLiked = module.ApplyTick(state, 1, [Like(Host, 9)]).State;
        Assert.NotEqual(module.ComputeStateHash(liked), module.ComputeStateHash(moreLiked));
        var earlier = new ExampleGameModuleV2();
        Assert.NotEqual(module.ComputeStateHash(liked), earlier.ComputeStateHash(liked));
    }

    [Fact]
    public void TheResultDocumentsTheAppliedLikesPerFactionForReconciliation()
    {
        var module = Module(threshold: 3, bonus: 10);
        var state = module.CreateInitialState(Config);
        state = module.ApplyTick(state, 1, [Like(Host, 4), Gift(Guest, 50)]).State;
        state = module.ApplyTick(state, 2, [Like(Host, 3), Boost(Host, 2)]).State;
        var final = module.ApplyTick(state, 3, []);

        Assert.True(final.Final);
        using var document = JsonDocument.Parse(final.ResultJson!);
        var root = document.RootElement;
        // Applied totals: red accepted 7 likes (4+3) → floor(7/3)=2 bursts → 20 bonus; blue 0.
        Assert.Equal(7L, LikeOf(root, Red));
        Assert.Equal(0L, LikeOf(root, Blue));
        Assert.Equal(20L + 24L, ScoreOf(root, Red)); // 2 bursts × 10 + a flat 12 × 2 boost
        Assert.Equal(50L, ScoreOf(root, Blue));
    }

    [Fact]
    public void TheLikeRuleKeepsTheGiftAndBoostScoringOfTheEarlierVersion()
    {
        var module = Module();
        var state = module.CreateInitialState(Config);
        state = module.ApplyTick(state, 1, [Gift(Host, 100), Boost(Guest, 3)]).State;

        // Unchanged 1.1.0 arithmetic: a gift scores its amount, a boost scores a flat 12 × level.
        Assert.Equal(100L, state.ScoreByFaction[Red]);
        Assert.Equal(36L, state.ScoreByFaction[Blue]);
    }

    private static long LikeOf(JsonElement result, string faction) =>
        result.GetProperty("likes").EnumerateArray()
            .Single(entry => entry.GetProperty("faction").GetString() == faction)
            .GetProperty("count").GetInt64();

    private static long ScoreOf(JsonElement result, string faction) =>
        result.GetProperty("ranking").EnumerateArray()
            .Single(entry => entry.GetProperty("faction").GetString() == faction)
            .GetProperty("score").GetInt64();
}
