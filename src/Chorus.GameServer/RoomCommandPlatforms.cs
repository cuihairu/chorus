namespace Chorus.GameServer;

/// <summary>
/// The input channels whose payloads are already module commands in the module's own shape —
/// the translator never touches them (FIX-SYNC-09 for clients, FIX-GM-07 for the operator's
/// delegated playtest command). One predicate for the live tick and the recovery replay: the
/// two must agree or a replayed room re-derives a different command and the state hash
/// diverges. A platform outside this set is a platform fact and goes through translation.
/// </summary>
internal static class RoomCommandPlatforms
{
    /// <summary>The direct client command channel (FIX-LINK-09).</summary>
    public const string Client = "@chorus";

    /// <summary>The operator-delegated playtest channel (FIX-GM-07, architecture 12.1).</summary>
    public const string Gm = "gm";

    /// <summary>The data-plane app id a GM dispatch is attributed under; the field is a
    /// required admission column, not an identity — the operation id is.</summary>
    public const string GmAppId = "gm";

    public static bool IsRawCommand(string platform) => platform is Client or Gm;
}
