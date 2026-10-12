using Chorus.Protocol.Models;

namespace Chorus.Engine.Abstractions;

public interface IGameModule : IDisposable
{
    string Name { get; }

    void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events);

    object GetStateSnapshot();
}

