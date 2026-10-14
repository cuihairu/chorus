namespace Chorus.EngineHost;

// WebApplicationFactory marker (tests target this namespaced type; the real
// entry point stays the top-level-statement Program in the global namespace).
// Net10 makes the generated Program public, so a global-namespace Program would
// be ambiguous across the three hosts - same pattern as Chorus.Server / Chorus.Gateway.
public partial class Program
{
}
