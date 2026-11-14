using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Chorus.DevTools;

/// <summary>
/// Forwards the host's ILogger pipeline onto the devtools channel: every entry
/// the LoggerFactory lets through becomes one dev.log envelope. The queue is
/// DropOldest bounded, so logging never blocks and never throws on a slow IDE —
/// the oldest history falls off instead.
/// </summary>
public sealed class DevToolsLoggerProvider(Channel<DevLogEntry> queue) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, DevToolsLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, static (category, q) => new DevToolsLogger(category, q.Writer), queue);

    public void Dispose()
    {
        // The queue belongs to the DI container and the service drains it for
        // as long as the host runs: provider disposal is not queue completion.
    }
}

/// <summary>The per-category logger: one entry per Log() call, carrying the
/// level, the category, the timestamp, and the formatted body (a thrown
/// exception appended beneath it, the console provider's shape).</summary>
internal sealed class DevToolsLogger(string category, ChannelWriter<DevLogEntry> writer) : ILogger
{
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var body = formatter(state, exception) ?? "";
        if (exception is not null) body = $"{body}{Environment.NewLine}{exception}";
        writer.TryWrite(new DevLogEntry(logLevel.ToString(), category,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), body));
    }

    // The host's own Logging:LogLevel filter decides what reaches the channel
    // (the factory applies it before the provider sees an entry); the
    // provider does not second-guess it.
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
