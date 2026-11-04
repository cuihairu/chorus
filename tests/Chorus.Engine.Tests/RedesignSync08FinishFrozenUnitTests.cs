using System.Text.Json;
using Chorus.ExampleGame;
using Chorus.GameServer;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-08 acceptance (模块冻结规则): every shipped module's pause-timeout rule derives
/// the terminal result from the frozen state — the canonical ranking/winner document matching
/// the scores the round froze at — without a new frame, without a tick advance, and without
/// mutating the frozen state (the persisted head's facts and hash are untouched; the caller
/// reads only the result document). The 1.2.0 module carries its likes section through; the
/// others never emit one. A frozen state with no score map refuses rather than inventing a
/// ranking, and a mismatched frozen tick is refused.
/// </summary>
public sealed class RedesignSync08FinishFrozenUnitTests
{
    private static readonly IReadOnlyDictionary<string, string> Factions =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["p1"] = GameStartConfig.RedFaction,
            ["p2"] = GameStartConfig.BlueFaction
        };

    private static ExampleGameState FrozenState(long tick = 7,
        IReadOnlyDictionary<string, long>? scores = null,
        IReadOnlyDictionary<string, long>? likes = null) =>
        new(tick, 444, 40, 0, Factions,
            scores ?? new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                [GameStartConfig.RedFaction] = 50,
                [GameStartConfig.BlueFaction] = 30
            },
            Finished: false, likes);

    [Fact]
    public void EveryShippedModuleDerivesTheFrozenRankingWithoutANewFrameOrTick()
    {
        foreach (var package in RedesignModuleCatalog.Shipped)
        {
            var module = package.Load(package.ManifestAt(25));
            var likes = new SortedDictionary<string, long>(StringComparer.Ordinal)
            {
                [GameStartConfig.RedFaction] = 2,
                [GameStartConfig.BlueFaction] = 1
            };
            var state = FrozenState(likes: likes);
            var hashBefore = module.ComputeStateHash(state);

            var outcome = module.FinishFrozen(state, state.Tick, "pause exceeded MaxPauseSeconds");

            // The rule ends the round from the frozen facts — no new frame, no tick advance,
            // and the input state object itself is untouched (the persisted head's facts and
            // hash are exactly what the room froze with).
            Assert.True(outcome.Final, package.Name + ": the freeze rule is not terminal.");
            Assert.Empty(outcome.Effects);
            Assert.Equal(state.Tick, outcome.State.Tick);
            Assert.Equal(state.ScoreByFaction, outcome.State.ScoreByFaction);
            Assert.True(outcome.State.Finished);
            Assert.False(state.Finished);
            Assert.Equal(hashBefore, module.ComputeStateHash(state));

            using var document = JsonDocument.Parse(outcome.ResultJson!);
            var root = document.RootElement;
            Assert.Equal("red", root.GetProperty("ranking")[0].GetProperty("faction").GetString());
            Assert.Equal(50, root.GetProperty("ranking")[0].GetProperty("score").GetInt64());
            Assert.Equal("blue", root.GetProperty("ranking")[1].GetProperty("faction").GetString());
            Assert.Equal(30, root.GetProperty("ranking")[1].GetProperty("score").GetInt64());
            Assert.Equal("red", root.GetProperty("winner").GetString());

            // The likes section rides only through the module that carries the like rule.
            var hasLikes = root.TryGetProperty("likes", out var likesElement);
            if (package.PackageHash == "pkg-1-v3")
            {
                // The likes section is ordered by faction name, and the rule derives it from
                // the frozen state's own like counts.
                Assert.True(hasLikes);
                Assert.Equal(2, likesElement.GetArrayLength());
                Assert.Equal("blue", likesElement[0].GetProperty("faction").GetString());
                Assert.Equal(1, likesElement[0].GetProperty("count").GetInt64());
                Assert.Equal("red", likesElement[1].GetProperty("faction").GetString());
                Assert.Equal(2, likesElement[1].GetProperty("count").GetInt64());
            }
            else
            {
                Assert.False(hasLikes, package.Name + " must not emit a likes section.");
            }
        }
    }

    [Fact]
    public void AStateWithNoScoreMapRefusesRatherThanInventingARanking()
    {
        var module = RedesignModuleCatalog.Default.Load(RedesignModuleCatalog.Default.ManifestAt(25));
        var empty = FrozenState(scores: new SortedDictionary<string, long>(StringComparer.Ordinal));
        Assert.Throws<InvalidOperationException>(() => module.FinishFrozen(empty, empty.Tick, "timeout"));
    }

    [Fact]
    public void AMismatchedFrozenTickIsRefused()
    {
        var module = RedesignModuleCatalog.Default.Load(RedesignModuleCatalog.Default.ManifestAt(25));
        var state = FrozenState(tick: 7);
        Assert.Throws<InvalidOperationException>(() => module.FinishFrozen(state, 8, "timeout"));
    }
}
