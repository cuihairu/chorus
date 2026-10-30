namespace Chorus.ExampleGame;

/// <summary>
/// The loadable unit of gameplay (architecture 8.1/8.4, FIX-MODULE-02): a package's
/// implementation behind the module catalog's loading entry. Modules of this family
/// share the state and IO records (<see cref="ExampleGameState"/>,
/// <see cref="GameStartConfig"/>, <see cref="NormalizedInput"/>, <see cref="TickOutcome"/>)
/// and the canonical result document shape — <c>{"ranking":[{faction,score}...],
/// "winner":...}</c> — the host derives the reward manifest from; the scoring rules and
/// which inputs move them are the module's own.
/// </summary>
public interface IRedesignGameModule
{
    /// <summary>The module facts — cadence, checkpoint policy, version — fixed by the
    /// module package, never by the host.</summary>
    RedesignGameModuleManifest Manifest { get; }

    ExampleGameState CreateInitialState(GameStartConfig config);

    /// <summary>Pure: consumes the ordered inputs and the current state, returns the next
    /// state with its effects and, at the final tick, the result document.</summary>
    TickOutcome ApplyTick(ExampleGameState state, long tick, IReadOnlyList<NormalizedInput> inputs);

    byte[] Serialize(ExampleGameState state);

    ExampleGameState Restore(byte[] payload, int schemaVersion);

    byte[] ComputeStateHash(ExampleGameState state);

    /// <summary>Control-input seam (architecture 8.2): when the host becomes unavailable
    /// the module must not invent reality from fresh reads.</summary>
    IReadOnlyList<NormalizedInput> OnHostUnavailable();
}
