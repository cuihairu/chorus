namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The tunable edge limits (contracts 3.1: one frame at most 64 KiB by default, login
/// attempts, control commands and acknowledgements rate-limited as separate classes). The
/// defaults are the contract values; the environment overrides exist for tests and for
/// operators with measured evidence, not for quiet relaxation.
/// </summary>
public sealed class EdgeOptions
{
    public int MaxMessageBytes { get; init; } = 64 * 1024;
    public int LoginPerMinute { get; init; } = 10;
    public int ControlPerMinute { get; init; } = 60;
    public int AckPerMinute { get; init; } = 600;
    public int MaxConnections { get; init; } = 10_000;
    /// <summary>The per-connection ordered delta queue (contracts 3.2 chain rule): a full
    /// queue refuses further deltas and only a snapshot re-baselines it.</summary>
    public int OutboxCapacity { get; init; } = 256;
    /// <summary>A connection that never completes its hello is closed within this window —
    /// the edge holds no unauthenticated sessions open (fail-closed).</summary>
    public TimeSpan PreAuthTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static EdgeOptions FromEnvironment() => new()
    {
        MaxMessageBytes = Int("CHORUS_MAX_MESSAGE_BYTES", 64 * 1024),
        LoginPerMinute = Int("CHORUS_LOGIN_PER_MINUTE", 10),
        ControlPerMinute = Int("CHORUS_CONTROL_PER_MINUTE", 60),
        AckPerMinute = Int("CHORUS_ACK_PER_MINUTE", 600),
        MaxConnections = Int("CHORUS_MAX_CONNECTIONS", 10_000),
        OutboxCapacity = Int("CHORUS_OUTBOX_CAPACITY", 256),
        PreAuthTimeout = TimeSpan.FromSeconds(Int("CHORUS_PREAUTH_TIMEOUT_SECONDS", 30)),
    };

    private static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value : fallback;
}
