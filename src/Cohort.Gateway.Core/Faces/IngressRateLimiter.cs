namespace Cohort.Gateway.Core.Faces;

/// <summary>
/// Fixed-window rate limiter for the platform-ingress face, keyed by request
/// source (remote IP). Thread-safe; per-key state is bounded by a sweeper that
/// removes keys idle for two windows once the table grows large.
/// </summary>
public sealed class IngressRateLimiter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (long WindowStartMs, int Count)> _windows = new(StringComparer.Ordinal);
    private readonly int _limitPerWindow;
    private readonly long _windowMs;

    public IngressRateLimiter(int limitPerMinute)
        : this(limitPerWindow: limitPerMinute, windowMs: 60_000)
    {
    }

    public IngressRateLimiter(int limitPerWindow, long windowMs)
    {
        _limitPerWindow = Math.Max(1, limitPerWindow);
        _windowMs = windowMs;
    }

    /// <summary>Returns true when the request is within the limit for the source key.</summary>
    public bool Allow(string sourceKey, long nowMs)
    {
        if (string.IsNullOrEmpty(sourceKey))
        {
            sourceKey = "unknown";
        }

        lock (_lock)
        {
            if (_windows.TryGetValue(sourceKey, out var window))
            {
                if (nowMs - window.WindowStartMs >= _windowMs)
                {
                    _windows[sourceKey] = (nowMs, 1);
                    return true;
                }

                if (window.Count >= _limitPerWindow)
                {
                    return false;
                }

                _windows[sourceKey] = (window.WindowStartMs, window.Count + 1);
                return true;
            }

            if (_windows.Count >= 10_000)
            {
                SweepExpired(nowMs);
            }

            _windows[sourceKey] = (nowMs, 1);
            return true;
        }
    }

    private void SweepExpired(long nowMs)
    {
        List<string>? expired = null;
        foreach (var (key, window) in _windows)
        {
            if (nowMs - window.WindowStartMs >= 2 * _windowMs)
            {
                (expired ??= new List<string>()).Add(key);
            }
        }

        if (expired != null)
        {
            foreach (var key in expired)
            {
                _windows.Remove(key);
            }
        }
    }
}
