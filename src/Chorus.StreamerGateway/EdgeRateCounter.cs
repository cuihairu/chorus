namespace Chorus.StreamerGateway;

/// <summary>
/// A fixed-window rate counter for one message class on one connection. The window is fixed,
/// not sliding: the count resets when the current minute rolls over, which is the contract's
/// "rate-limited per class" in its simplest testable form. The clock is injectable so tests
/// drive window rollovers deterministically instead of sleeping.
/// </summary>
public sealed class EdgeRateCounter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _windowStart;
    private int _count;
    private long _rejected;

    public EdgeRateCounter(int limit, TimeSpan window, Func<DateTimeOffset>? now = null)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        _window = window;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _windowStart = _now();
    }

    public long Rejected => _rejected;

    /// <summary>Consumes one unit if the class still has budget in this window.</summary>
    public bool TryConsume()
    {
        var now = _now();
        if (now - _windowStart >= _window)
        {
            _windowStart = now;
            _count = 0;
        }
        if (_count >= _limit)
        {
            _rejected++;
            return false;
        }
        _count++;
        return true;
    }
}
