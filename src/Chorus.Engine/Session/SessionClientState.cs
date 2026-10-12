namespace Chorus.Engine.Session;

public sealed class SessionClientState
{
    public required string ClientId { get; init; }
    public long ConnectedTickId { get; init; }
    public long LastAckTickId { get; set; }
    public long LastAckTimeMs { get; set; }
    public long LastResyncTimeMs { get; set; }
    public long ResyncCount { get; set; }
}
