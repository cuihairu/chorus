using Chorus.Protocol.Models;

namespace Chorus.Engine.Session;

/// <summary>Authoritative input/checkpoint boundary. No snapshot is published before a successful commit.</summary>
public interface ISessionTickDurability
{
    Task<IReadOnlyList<AudienceEvent>> PrepareAsync(CancellationToken cancellationToken);
    Task CommitAsync(long expectedTick, long tick, object state, IReadOnlyList<AudienceEvent> consumed, CancellationToken cancellationToken);
}
