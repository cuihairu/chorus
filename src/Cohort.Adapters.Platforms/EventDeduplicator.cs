using Microsoft.Extensions.Caching.Memory;

namespace Cohort.Adapters.Platforms;

public sealed class EventDeduplicator
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public EventDeduplicator(IMemoryCache cache, TimeSpan ttl)
    {
        _cache = cache;
        _ttl = ttl;
    }

    public bool TryMark(string platform, string eventId)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(eventId))
        {
            return true;
        }

        var key = $"{platform}:{eventId}";
        if (_cache.TryGetValue(key, out _))
        {
            return false;
        }

        _cache.Set(key, true, _ttl);
        return true;
    }
}
