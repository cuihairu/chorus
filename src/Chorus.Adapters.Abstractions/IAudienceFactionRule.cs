using Chorus.Protocol.Models;

namespace Chorus.Adapters.Abstractions;

public interface IAudienceFactionRule
{
    AudienceEvent Apply(AudienceEvent e);
}
