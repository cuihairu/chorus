using Chorus.Protocol.Models;

namespace Chorus.Engine.Session;

public sealed record AudienceEventReduceResult(
    IReadOnlyList<AudienceEvent> Events,
    int MergedInputEvents,
    int DroppedInputEvents
);

public interface IAudienceEventReducer
{
    AudienceEventReduceResult Reduce(IReadOnlyList<AudienceEvent> events, int maxEventsPerTick);
}
