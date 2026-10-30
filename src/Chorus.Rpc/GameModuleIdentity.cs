namespace Chorus.Rpc;

/// <summary>
/// The cluster's declared identity of the default module it ships (FIX-MODULE-01): the
/// single authority for the package, module version, configuration pin and
/// recovery/checkpoint mode a room is admitted under by default. Every writer — the
/// coordinator that starts a room, the host that prepares its tick-0 snapshot — pins the
/// room's identity from here, and a recovery reads the room's persisted identity back
/// and refuses any room whose identity this host cannot load, instead of silently
/// rebuilding the module from the caller's request.
/// </summary>
/// <remarks>
/// Since FIX-MODULE-02 the loading authority is the package registry
/// (<c>RedesignModuleCatalog</c> in the game host): this identity names the registry's
/// default package — the one the coordinator pins for every room it starts — while the
/// registry also ships further packages (the duel test module), and both prepare and
/// recovery resolve the room's package hash through it. A room persisted under a hash
/// the registry does not carry is refused at recovery with the missing-package contract
/// error. The config hash and package hash are the cluster's declared pins of the
/// shipped module; the <em>effective</em> configuration at replay is the frame-carried
/// hash the module derived when it prepared the room (the two are intentionally
/// distinct: a pin names the declared package, a frame hash names the state the room
/// actually runs).
/// </remarks>
public static class GameModuleIdentity
{
    /// <summary>The default module's declared name.</summary>
    public const string Name = "example-factions";

    /// <summary>The default module's version label. A room persisted under any other
    /// version is refused at recovery with the incompatible-version contract error.</summary>
    public const string ModuleVersion = "1.0.0";

    /// <summary>The declared configuration pin written to the room row at start.</summary>
    public const string ConfigHash = "cfg-1";

    /// <summary>The default package this cluster loads. A room persisted under any other
    /// package is refused at recovery with the missing-package contract error.</summary>
    public const string PackageHash = "pkg-1";

    /// <summary>The default module's recovery/checkpoint mode (the manifest
    /// CheckpointMode). A room persisted under any other mode is refused at recovery
    /// with the incompatible checkpoint-mode contract error.</summary>
    public const string RecoveryMode = "DeterministicReplay";
}
