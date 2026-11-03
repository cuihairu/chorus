namespace Chorus.ExampleGame;

/// <summary>
/// The room's input boundary (FIX-INPUT-08): a platform delivery arrives in the platform's
/// normalized event shape; the module consumes its own command shape. The translator is the
/// configured mapping between the two, applied identically on the live tick and on the
/// recovery replay so both derive the same frames — and the same state hashes — from the
/// same deliveries. The host passes the delivered payload through this seam once, when it
/// builds the tick's module inputs; the stored delivery payload itself is never rewritten.
/// </summary>
public interface IRoomInputTranslator
{
    /// <summary>The module command the payload carries, or null when the payload is not a
    /// module command (the input is still consumed and referenced by its frame, it just
    /// produces no module effect).</summary>
    string? Translate(string payloadJson);
}
