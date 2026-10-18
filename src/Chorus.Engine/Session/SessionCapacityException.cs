namespace Chorus.Engine.Session;

/// <summary>A retryable admission failure; no existing room was evicted.</summary>
public sealed class SessionCapacityException(int capacity)
    : InvalidOperationException($"Worker session capacity ({capacity}) reached.")
{
    public int Capacity { get; } = capacity;
}
