using Chorus.ExampleGame;
using Chorus.Rpc;

namespace Chorus.GameServer;

/// <summary>
/// One shipped module package (FIX-MODULE-02): the identity facts a room pins at
/// admission (FIX-MODULE-01) together with the loading entry that builds the module a
/// prepare or a recovery runs. Keyed by package hash — never by module name — so two
/// versions of one module can ship side by side (FIX-MODULE-04's upgrade window).
/// </summary>
/// <param name="Load">Builds the module under a manifest derived from this package's
/// facts and the room's cadence. Called once per room; must be pure — the same package
/// always builds behaviorally the same module.</param>
public sealed record RedesignModulePackage(
    string Name, string ModuleVersion, string ConfigHash, string PackageHash,
    string RecoveryMode, int TickMs, int SnapshotEveryTicks,
    Func<RedesignGameModuleManifest, IRedesignGameModule> Load)
{
    /// <summary>The manifest this package's module runs a room at: the package's own
    /// facts with the room's persisted cadence.</summary>
    public RedesignGameModuleManifest ManifestAt(int tickMs) =>
        new(Name, ModuleVersion, tickMs, SnapshotEveryTicks, RecoveryMode);
}

/// <summary>
/// The module registry (FIX-MODULE-02): this host's shipped packages, the loading entry
/// every prepare and recovery goes through — no call site may build a game module by
/// hand. <see cref="Default"/> is the production package (the identity
/// <see cref="GameModuleIdentity"/> declared authoritative in FIX-MODULE-01); the duel
/// package is the second shipped module whose rooms prove the entry dispatches on the
/// room's package instead of falling back to the example module.
/// </summary>
public static class RedesignModuleCatalog
{
    /// <summary>The production package: the example factions module.</summary>
    public static RedesignModulePackage Default { get; } = new(
        GameModuleIdentity.Name, GameModuleIdentity.ModuleVersion, GameModuleIdentity.ConfigHash,
        GameModuleIdentity.PackageHash, GameModuleIdentity.RecoveryMode,
        RedesignGameModuleManifest.Default.TickMs, RedesignGameModuleManifest.Default.SnapshotEveryTicks,
        static manifest => new ExampleGameModule(manifest));

    /// <summary>The duel package (FIX-MODULE-02's loading evidence): same state family,
    /// boost-only scoring — the module a room under <c>pkg-duel-1</c> actually runs.</summary>
    public static RedesignModulePackage Duel { get; } = new(
        "example-duel", "0.9.0", "cfg-duel-1", "pkg-duel-1",
        RedesignGameModuleManifest.DeterministicReplay,
        RedesignGameModuleManifest.FastTest.TickMs, RedesignGameModuleManifest.Default.SnapshotEveryTicks,
        static manifest => new DuelGameModule(manifest));

    /// <summary>The live package (FIX-MODULE-03's evidence): an honestly
    /// non-deterministic module (clock jitter in every boost) under FullCheckpoint — its
    /// rooms checkpoint every committed frame and recover without replay.</summary>
    public static RedesignModulePackage Live { get; } = new(
        "example-live", "0.9.0", "cfg-live-1", "pkg-live-1",
        RedesignGameModuleManifest.FullCheckpoint,
        RedesignGameModuleManifest.FastTest.TickMs, RedesignGameModuleManifest.Default.SnapshotEveryTicks,
        static manifest => new LiveGameModule(manifest));

    /// <summary>Every package this host ships, in stable order; the hashes are what a
    /// missing-package refusal lists back to the caller.</summary>
    public static IReadOnlyList<RedesignModulePackage> Shipped { get; } = [Default, Duel, Live];

    private static readonly IReadOnlyDictionary<string, RedesignModulePackage> ByPackageHash =
        BuildIndex();

    /// <summary>Resolves a package by its hash. Empty names the cluster's default package
    /// (the same pin the coordinator writes); a hash this host does not ship answers
    /// false — the caller must refuse by name, never fall back to the default package.</summary>
    public static bool TryGet(string packageHash, out RedesignModulePackage package)
    {
        if (packageHash.Length > 0)
            return ByPackageHash.TryGetValue(packageHash, out package!);
        package = Default;
        return true;
    }

    /// <summary>The comma-joined shipped package hashes, for refusal notes that say what
    /// this host can load.</summary>
    public static string ShippedList() => string.Join(", ",
        Shipped.Select(package => package.PackageHash).Order(StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, RedesignModulePackage> BuildIndex()
    {
        var index = new Dictionary<string, RedesignModulePackage>(StringComparer.Ordinal);
        foreach (var package in Shipped)
        {
            if (!index.TryAdd(package.PackageHash, package))
                throw new InvalidOperationException(
                    $"Two shipped module packages share the hash {package.PackageHash}; " +
                    "a package hash names exactly one package.");
        }
        return index;
    }
}
