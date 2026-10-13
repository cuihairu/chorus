namespace Chorus.Engine.Session;

public sealed class SessionClientState
{
    public required string ClientId { get; init; }
    public long ConnectedTickId { get; init; }
    public long LastAckTickId { get; set; }
    public long LastAckTimeMs { get; set; }
    public long LastResyncTimeMs { get; set; }
    public long ResyncCount { get; set; }
    /// <summary>Last client-initiated resync request time (frame-sync P2 cooldown).</summary>
    public long LastResyncRequestTimeMs { get; set; }
}
