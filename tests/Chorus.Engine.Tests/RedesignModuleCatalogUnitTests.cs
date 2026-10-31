using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-MODULE-02, the registry leg: the shipped packages are the loading authority — the
/// default package is exactly the identity FIX-MODULE-01 declared (the coordinator's pin
/// and the recovery contract keep their meaning), the duel package resolves under its
/// own hash, an unknown hash answers false, and the two packages' modules genuinely
/// diverge (the same inputs produce different outcomes, so loading the wrong one is
/// detectable, not cosmetic).
/// </summary>
public sealed class RedesignModuleCatalogUnitTests
{
    [Fact]
    public void TheDefaultPackageIsTheIdentityFixModule01Declared()
    {
        var package = RedesignModuleCatalog.Default;

        Assert.Equal(GameModuleIdentity.Name, package.Name);
        Assert.Equal(GameModuleIdentity.ModuleVersion, package.ModuleVersion);
        Assert.Equal(GameModuleIdentity.ConfigHash, package.ConfigHash);
        Assert.Equal(GameModuleIdentity.PackageHash, package.PackageHash);
        Assert.Equal(GameModuleIdentity.RecoveryMode, package.RecoveryMode);
        // Empty names the default package — the pin the coordinator writes at StartRound.
        Assert.True(RedesignModuleCatalog.TryGet("", out var empty));
        Assert.Equal(RedesignModuleCatalog.Default.PackageHash, empty.PackageHash);
    }

    [Fact]
    public void EveryShippedPackageResolvesUnderItsOwnHashAndUnknownHashesDoNot()
    {
        foreach (var shipped in RedesignModuleCatalog.Shipped)
        {
            Assert.True(RedesignModuleCatalog.TryGet(shipped.PackageHash, out var resolved));
            Assert.Equal(shipped, resolved);
        }

        Assert.False(RedesignModuleCatalog.TryGet("pkg-never-shipped", out _));
        Assert.Contains("pkg-1", RedesignModuleCatalog.ShippedList());
        Assert.Contains("pkg-duel-1", RedesignModuleCatalog.ShippedList());
    }

    [Fact]
    public void TheTwoPackagesGenuinelyDivergeOnTheSameInputs()
    {
        var example = RedesignModuleCatalog.Default.Load(RedesignModuleCatalog.Default.ManifestAt(25));
        var duel = RedesignModuleCatalog.Duel.Load(RedesignModuleCatalog.Duel.ManifestAt(25));
        var config = new GameStartConfig(200, 777,
            [new GameParticipant("host-cat", GameStartConfig.RedFaction),
             new GameParticipant("guest-cat", GameStartConfig.BlueFaction)], 0);

        // A gift moves the example module and is noise to the duel.
        var byGift = new[] { new NormalizedInput("host-cat", """{"kind":"gift","amount":7}""") };
        var exampleGift = example.ApplyTick(example.CreateInitialState(config), 1, byGift);
        var duelGift = duel.ApplyTick(duel.CreateInitialState(config), 1, byGift);
        Assert.Single(exampleGift.Effects);
        Assert.Empty(duelGift.Effects);

        // A boost scores in both, differently: the example adds its seed draw, the duel
        // is flat — the state hashes part ways on the very first tick.
        var byBoost = new[] { new NormalizedInput("host-cat", """{"action":"boost","level":3}""") };
        var exampleBoost = example.ApplyTick(example.CreateInitialState(config), 1, byBoost);
        var duelBoost = duel.ApplyTick(duel.CreateInitialState(config), 1, byBoost);
        Assert.Equal(30L, duelBoost.State.ScoreByFaction[GameStartConfig.RedFaction]);
        // The example adds its carried seed's draw on top of the flat points.
        Assert.InRange(exampleBoost.State.ScoreByFaction[GameStartConfig.RedFaction], 30, 34);
        Assert.NotEqual(
            Convert.ToHexString(example.ComputeStateHash(exampleBoost.State)),
            Convert.ToHexString(duel.ComputeStateHash(duelBoost.State)));

        // Both serialize and restore their own state — a duel room's snapshot rebuilds
        // under the duel module byte-identically.
        var restored = duel.Restore(duel.Serialize(duelGift.State), duelGift.State.SchemaVersion);
        Assert.Equal(
            Convert.ToHexString(duel.ComputeStateHash(duelGift.State)),
            Convert.ToHexString(duel.ComputeStateHash(restored)));
    }

    [Fact]
    public void TheLivePackageShipsFullCheckpointAndHonestNonDeterminism()
    {
        // FIX-MODULE-03: the third shipped package declares FullCheckpoint — the mode
        // that checkpoints every committed tick — and the module it loads is honestly
        // non-deterministic, which the clock seam proves without a flaky wall-clock
        // assertion: under one fixed clock value the same inputs rebuild the same
        // state, across clock values they never do, so deterministic replay of a live
        // room is impossible by construction and only the checkpoint can recover it.
        Assert.True(RedesignModuleCatalog.TryGet("pkg-live-1", out var live));
        Assert.Equal("example-live", live.Name);
        Assert.Equal("0.9.0", live.ModuleVersion);
        Assert.Equal("cfg-live-1", live.ConfigHash);
        Assert.Equal(RedesignGameModuleManifest.FullCheckpoint, live.RecoveryMode);
        Assert.Equal(RedesignGameModuleManifest.FastTest.TickMs, live.TickMs);
        Assert.Contains("pkg-live-1", RedesignModuleCatalog.ShippedList());

        var config = new GameStartConfig(200, 777,
            [new GameParticipant("host-live", GameStartConfig.RedFaction),
             new GameParticipant("guest-live", GameStartConfig.BlueFaction)], 0);
        var boost = new[] { new NormalizedInput("host-live", """{"action":"boost","level":3}""") };
        var atFour = new LiveGameModule(live.ManifestAt(25), clock: () => 4L); // jitter 1
        var atFive = new LiveGameModule(live.ManifestAt(25), clock: () => 5L); // jitter 2

        // Same clock value: the tick is reproducible (two runs hash identically).
        var first = atFour.ApplyTick(atFour.CreateInitialState(config), 1, boost);
        var again = atFour.ApplyTick(atFour.CreateInitialState(config), 1, boost);
        Assert.Equal(
            Convert.ToHexString(atFour.ComputeStateHash(first.State)),
            Convert.ToHexString(atFour.ComputeStateHash(again.State)));
        Assert.Equal(31L, first.State.ScoreByFaction[GameStartConfig.RedFaction]); // 30 + jitter 1

        // Different clock values: the same inputs never rebuild the same state — the
        // hash chain a replay depends on cannot exist across wall-clock time.
        var later = atFive.ApplyTick(atFive.CreateInitialState(config), 1, boost);
        Assert.Equal(32L, later.State.ScoreByFaction[GameStartConfig.RedFaction]); // 30 + jitter 2
        Assert.NotEqual(
            Convert.ToHexString(atFour.ComputeStateHash(first.State)),
            Convert.ToHexString(atFive.ComputeStateHash(later.State)));

        // The manifest the package builds a room at carries the mode through, so the
        // host derives its checkpoint cadence from the package, not a global default.
        Assert.Equal(RedesignGameModuleManifest.FullCheckpoint,
            live.ManifestAt(25).CheckpointMode);
    }
}
