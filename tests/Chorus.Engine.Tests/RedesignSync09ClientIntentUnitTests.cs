using Chorus.ExampleGame;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-09, the module leg of the client-intent boundary (authoritative-sync.md:69-78):
/// a <see cref="NormalizedInput"/> now carries its origin, and the modules hold the trust
/// boundary — a client command (FromClient) is an operation intent and may never enter the
/// platform fact channel (kind), while a platform event may. The action-size cap bounds the
/// one intent every shipped module scores (boost): a level beyond <see cref="ExampleGameModule.MaxBoostLevel"/>
/// is an illegal action, ignored like an unknown shape instead of scoring unbounded.
/// </summary>
public sealed class RedesignSync09ClientIntentUnitTests
{
    private static GameStartConfig Config(long seed = 777) =>
        new(200, seed,
            [new GameParticipant("host-9", GameStartConfig.RedFaction),
             new GameParticipant("guest-9", GameStartConfig.BlueFaction)], 0);

    [Fact]
    public void V1ClientGiftIsIgnoredAndPlatformGiftScores()
    {
        var module = new ExampleGameModule();
        var state = module.CreateInitialState(Config());
        const string gift = """{"kind":"gift","amount":7}""";

        var client = module.ApplyTick(state, 1, [new NormalizedInput("host-9", gift, FromClient: true)]);
        Assert.Empty(client.Effects);
        Assert.Equal(0L, client.State.ScoreByFaction[GameStartConfig.RedFaction]);

        var platform = module.ApplyTick(state, 1, [new NormalizedInput("host-9", gift, FromClient: false)]);
        Assert.Single(platform.Effects);
        Assert.Equal(7L, platform.State.ScoreByFaction[GameStartConfig.RedFaction]);
    }

    [Fact]
    public void V2ClientGiftIsIgnoredAndPlatformGiftScores()
    {
        var module = new ExampleGameModuleV2();
        var state = module.CreateInitialState(Config());
        const string gift = """{"kind":"gift","amount":7}""";

        var client = module.ApplyTick(state, 1, [new NormalizedInput("host-9", gift, FromClient: true)]);
        Assert.Empty(client.Effects);
        Assert.Equal(0L, client.State.ScoreByFaction[GameStartConfig.RedFaction]);

        var platform = module.ApplyTick(state, 1, [new NormalizedInput("host-9", gift, FromClient: false)]);
        Assert.Single(platform.Effects);
        Assert.Equal(7L, platform.State.ScoreByFaction[GameStartConfig.RedFaction]);
    }

    [Fact]
    public void V3ClientGiftAndLikeAreIgnoredAndPlatformFactsApply()
    {
        var module = new ExampleGameModuleV3();
        var state = module.CreateInitialState(Config());

        var clientGift = module.ApplyTick(state, 1,
            [new NormalizedInput("host-9", """{"kind":"gift","amount":7}""", FromClient: true)]);
        Assert.Empty(clientGift.Effects);
        Assert.Equal(0L, clientGift.State.ScoreByFaction[GameStartConfig.RedFaction]);
        Assert.Equal(0L, clientGift.State.LikeByFaction![GameStartConfig.RedFaction]);

        var clientLike = module.ApplyTick(state, 1,
            [new NormalizedInput("host-9", """{"kind":"like","count":5}""", FromClient: true)]);
        Assert.Equal(0L, clientLike.State.LikeByFaction![GameStartConfig.RedFaction]);

        var platformGift = module.ApplyTick(state, 1,
            [new NormalizedInput("host-9", """{"kind":"gift","amount":7}""", FromClient: false)]);
        Assert.Equal(7L, platformGift.State.ScoreByFaction[GameStartConfig.RedFaction]);

        var platformLike = module.ApplyTick(state, 1,
            [new NormalizedInput("host-9", """{"kind":"like","count":5}""", FromClient: false)]);
        Assert.Equal(5L, platformLike.State.LikeByFaction![GameStartConfig.RedFaction]);
    }

    [Fact]
    public void AClientBoostIsStillAnIntentAndScores()
    {
        var module = new ExampleGameModuleV3();
        var state = module.CreateInitialState(Config());
        var outcome = module.ApplyTick(state, 1,
            [new NormalizedInput("host-9", """{"action":"boost","level":2}""", FromClient: true)]);
        Assert.Equal(24L, outcome.State.ScoreByFaction[GameStartConfig.RedFaction]);
    }

    [Fact]
    public void BoostLevelBeyondTheCapIsIgnoredInEveryModule()
    {
        var modules = new (IRedesignGameModule Module, string Name)[]
        {
            (new ExampleGameModule(), "V1"),
            (new ExampleGameModuleV2(), "V2"),
            (new ExampleGameModuleV3(), "V3"),
            (new LiveGameModule(clock: () => 0), "Live"),
            (new DuelGameModule(), "Duel")
        };
        foreach (var (module, name) in modules)
        {
            var config = Config();
            // The cap's upper edge: level 3 is a legal boost and scores.
            var ok = module.ApplyTick(module.CreateInitialState(config), 1,
                [new NormalizedInput("host-9", """{"action":"boost","level":3}""", FromClient: false)]);
            Assert.True(ok.State.ScoreByFaction[GameStartConfig.RedFaction] > 0L,
                name + " level 3 must score");
            // One beyond the cap is an illegal action, ignored — never an unbounded score.
            var over = module.ApplyTick(module.CreateInitialState(config), 1,
                [new NormalizedInput("host-9", """{"action":"boost","level":4}""", FromClient: false)]);
            Assert.True(over.State.ScoreByFaction[GameStartConfig.RedFaction] == 0L,
                name + " level 4 must be ignored");
            // Zero, negative and absent levels are illegal too — the same deterministic ignore.
            var zero = module.ApplyTick(module.CreateInitialState(config), 1,
                [new NormalizedInput("host-9", """{"action":"boost","level":0}""", FromClient: false)]);
            Assert.True(zero.State.ScoreByFaction[GameStartConfig.RedFaction] == 0L,
                name + " level 0 must be ignored");
            var negative = module.ApplyTick(module.CreateInitialState(config), 1,
                [new NormalizedInput("host-9", """{"action":"boost","level":-1}""", FromClient: false)]);
            Assert.True(negative.State.ScoreByFaction[GameStartConfig.RedFaction] == 0L,
                name + " negative level must be ignored");
            var absent = module.ApplyTick(module.CreateInitialState(config), 1,
                [new NormalizedInput("host-9", """{"action":"boost"}""", FromClient: false)]);
            Assert.True(absent.State.ScoreByFaction[GameStartConfig.RedFaction] == 0L,
                name + " absent level must be ignored");
        }
    }
}
