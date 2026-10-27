using System.Text.Json;
using Chorus.Redesign.ExampleGame;

namespace Chorus.Engine.Tests;

/// <summary>
/// Module-contract evidence for the example gameplay (architecture 8.4): the same start
/// configuration and input script always rebuild the same state, snapshots restore bit-for-bit
/// with the RNG seed inside, unknown schema versions fail closed, and the deterministic fault
/// seam fires at its tick. Cross-OS replay stays an M4 acceptance item (AT16).
/// </summary>
public sealed class ExampleGameModuleTests
{
    [Fact]
    public void ApplyTickIsDeterministicAndReplayableFromSnapshot()
    {
        var module = new ExampleGameModule();
        var state = module.CreateInitialState(Config());
        var once = Play(module, state,
            [Gift("p1", 10)],
            [Gift("p2", 7), Boost("p1", 3)],
            Array.Empty<NormalizedInput>(),
            [Gift("p1", 5), Gift("p2", 2)]);

        // A fresh module rebuilds the identical state from the same configuration and script.
        var again = Play(new ExampleGameModule(), module.CreateInitialState(Config()),
            [Gift("p1", 10)],
            [Gift("p2", 7), Boost("p1", 3)],
            Array.Empty<NormalizedInput>(),
            [Gift("p1", 5), Gift("p2", 2)]);
        Assert.Equal(module.ComputeStateHash(once), module.ComputeStateHash(again));
        Assert.Equal(module.Serialize(once), module.Serialize(again));
        Assert.Equal(4, once.Tick);

        // Restore from the persisted snapshot and continue: the next tick hashes identically,
        // because the RNG seed travelled inside the snapshot instead of being redrawn.
        var restored = module.Restore(module.Serialize(once), once.SchemaVersion);
        Assert.Equal(once.Seed, restored.Seed);
        Assert.Equal(once.ScoreByFaction, restored.ScoreByFaction);
        var fromLive = module.ApplyTick(once, once.Tick + 1, [Gift("p1", 1)]);
        var fromRestored = module.ApplyTick(restored, restored.Tick + 1, [Gift("p1", 1)]);
        Assert.Equal(module.ComputeStateHash(fromLive.State), module.ComputeStateHash(fromRestored.State));
    }

    [Fact]
    public void FinalTickFreezesTheRankingAndIgnoresUnknownShapes()
    {
        var module = new ExampleGameModule();
        var state = module.CreateInitialState(Config(durationTicks: 2));

        var first = module.ApplyTick(state, 1, [Gift("p1", 30), Unknown("p1"), Gift("p9", 100)]);
        // Unknown payload shapes and unknown participants contribute nothing, deterministically.
        Assert.Equal(30L, first.State.ScoreByFaction[GameStartConfig.RedFaction]);
        Assert.Equal(0L, first.State.ScoreByFaction[GameStartConfig.BlueFaction]);
        var score = Assert.Single(first.Effects);
        Assert.Equal(ExampleGameModule.ScoreEffect, score.Kind);
        using (var document = JsonDocument.Parse(score.PayloadJson))
        {
            Assert.Equal(GameStartConfig.RedFaction, document.RootElement.GetProperty("faction").GetString());
            Assert.Equal(30, document.RootElement.GetProperty("delta").GetInt32());
            Assert.Equal(30, document.RootElement.GetProperty("total").GetInt64());
        }
        Assert.False(first.Final);

        var final = module.ApplyTick(first.State, 2, [Gift("p2", 5)]);
        Assert.True(final.Final);
        Assert.Equal(module.ComputeStateHash(final.State),
            module.ComputeStateHash(module.Restore(module.Serialize(final.State), 1)));
        using (var document = JsonDocument.Parse(final.ResultJson!))
        {
            Assert.Equal("red", document.RootElement.GetProperty("winner").GetString());
            var ranking = document.RootElement.GetProperty("ranking");
            Assert.Equal(2, ranking.GetArrayLength());
            Assert.Equal(GameStartConfig.RedFaction, ranking[0].GetProperty("faction").GetString());
            Assert.Equal(30, ranking[0].GetProperty("score").GetInt64());
            Assert.Equal(GameStartConfig.BlueFaction, ranking[1].GetProperty("faction").GetString());
        }

        // An even score freezes as a draw with the ranking ordered by faction name.
        var draw = module.CreateInitialState(Config(durationTicks: 1));
        var drawn = module.ApplyTick(draw, 1, Array.Empty<NormalizedInput>());
        using (var document = JsonDocument.Parse(drawn.ResultJson!))
        {
            Assert.Equal("draw", document.RootElement.GetProperty("winner").GetString());
            Assert.Equal(GameStartConfig.BlueFaction,
                document.RootElement.GetProperty("ranking")[0].GetProperty("faction").GetString());
        }
    }

    [Fact]
    public void ModuleContractFailsClosed()
    {
        var module = new ExampleGameModule();
        // Unknown snapshot schema versions are refused, never guessed (restore fail-closed).
        Assert.Throws<ArgumentException>(() => module.Restore(module.Serialize(
            module.CreateInitialState(Config())), 2));

        // Ticks advance one at a time; a finished round does not tick.
        var state = module.CreateInitialState(Config(durationTicks: 2));
        Assert.Throws<InvalidOperationException>(() => module.ApplyTick(state, 2, []));
        var finished = Play(module, state, [], []);
        Assert.True(finished.Finished);
        Assert.Throws<InvalidOperationException>(() => module.ApplyTick(finished, 3, []));

        // The deterministic fault seam fires exactly from its tick.
        var faulted = module.CreateInitialState(Config(failAfterTick: 2));
        Assert.Throws<InvalidOperationException>(() => Play(module, faulted, [], [], []));

        // The start configuration is validated: factions are red or blue, participants distinct.
        Assert.Throws<ArgumentException>(() => module.CreateInitialState(new GameStartConfig(2, 1,
            [new GameParticipant("p1", "green")])));
        Assert.Throws<ArgumentException>(() => module.CreateInitialState(new GameStartConfig(2, 1,
            [new GameParticipant("p1", GameStartConfig.RedFaction),
                new GameParticipant("p1", GameStartConfig.BlueFaction)])));

        // The manifest pins the cadence and the checkpoint policy; the host cannot relax either.
        Assert.Equal(50, RedesignGameModuleManifest.Default.TickMs);
        Assert.Equal(25, RedesignGameModuleManifest.FastTest.TickMs);
        Assert.Equal(100, RedesignGameModuleManifest.Default.SnapshotEveryTicks);
        Assert.Equal(RedesignGameModuleManifest.DeterministicReplay,
            RedesignGameModuleManifest.Default.CheckpointMode);

        // Host loss yields no control inputs: the module never invents reality from fresh reads.
        Assert.Empty(module.OnHostUnavailable());
    }

    private static GameStartConfig Config(long seed = 77, long durationTicks = 8, int failAfterTick = 0) =>
        new(durationTicks, seed,
        [
            new GameParticipant("p1", GameStartConfig.RedFaction),
            new GameParticipant("p2", GameStartConfig.BlueFaction)
        ], failAfterTick);

    private static ExampleGameState Play(ExampleGameModule module, ExampleGameState state,
        params NormalizedInput[][] ticks)
    {
        foreach (var inputs in ticks) state = module.ApplyTick(state, state.Tick + 1, inputs).State;
        return state;
    }

    private static NormalizedInput Gift(string participant, int amount) =>
        new(participant, $$"""{"kind": "gift", "amount": {{amount}}}""");

    private static NormalizedInput Boost(string participant, int level) =>
        new(participant, $$"""{"action": "boost", "level": {{level}}}""");

    private static NormalizedInput Unknown(string participant) =>
        new(participant, """{"kind": "emote", "face": "grin"}""");
}
