namespace Chorus.Control;

/// <summary>One bots scenario: the operator's TOML script text plus its panel name,
/// stored as the operator left it (BOTS-2). Validation happens when a run parses the
/// script — the store keeps the text as given, so a scenario under edit is never
/// silently rewritten.</summary>
public sealed record BotScenario(string Id, string Name, string Toml, string UpdatedBy, long UpdatedAtMs);

/// <summary>
/// The bots scenarios the console's BotsPage manages (BOTS-2): plain records in the
/// control plane's generic records table, so scenarios survive control-plane restarts
/// the way every other control-plane record does. Runs themselves are the fleet
/// service's in-memory registry — a run is ephemeral by nature.
/// </summary>
public sealed class BotScenarioStore(ControlDatabase database)
{
    /// <summary>The control plane's own table name for scenarios.</summary>
    public const string Kind = "bot_scenario";

    public IReadOnlyList<BotScenario> List() =>
        database.Read(t => t.List<BotScenario>(Kind));

    public BotScenario? Get(string id) =>
        database.Read(t => t.Get<BotScenario>(Kind, id));

    public BotScenario Save(BotScenario scenario) =>
        database.Write(t => { t.Put(Kind, scenario.Id, scenario); return scenario; });

    public bool Delete(string id) =>
        database.Write(t =>
        {
            if (t.Get<BotScenario>(Kind, id) is null) return false;
            t.Delete(Kind, id);
            return true;
        });
}
