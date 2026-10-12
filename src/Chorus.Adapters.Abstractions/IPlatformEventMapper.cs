using Chorus.Protocol.Models;

namespace Chorus.Adapters.Abstractions;

public interface IPlatformEventMapper
{
    AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs);
}

