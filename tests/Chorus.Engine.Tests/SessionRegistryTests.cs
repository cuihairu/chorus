using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;

namespace Chorus.Engine.Tests;

/// <summary>
/// Direct acceptance for the shared session lifecycle (todo §3.5 router
/// abstraction, first slice): SessionRegistry is the single implementation of
/// the session cap (idlest-first eviction), the client-less idle sweep, and
/// last-seen tracking, used by both the monolith's SessionManager and the
/// split mode's EngineIpcService.
/// </summary>
public sealed class SessionRegistryTests
{
    private sealed class StubGameModule : IGameModule
    {
        public string Name => "stub-registry";
        public object GetStateSnapshot() => new { };
        public void ApplyEvents(long tickId, IReadOnlyList<Chorus.Protocol.Models.AudienceEvent> events) { }
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }

        public void Dispose() { }
    }

    private static SessionRegistry CreateRegistry(
        int maxSessions = 0,
        int idleEvictAfterMs = 600_000,
        HashSet<string>? withClients = null)
    {
        return new SessionRegistry(
            new SessionConfig
            {
                TickDurationMs = 10,
                SnapshotEveryTicks = 0,
                MaxSessions = maxSessions,
                IdleEvictAfterMs = idleEvictAfterMs,
            },
            sessionId => CreateStartedActor(sessionId),
            sessionId => withClients?.Contains(sessionId) ?? false,
            runSweepLoop: false);
    }

    private static SessionActor CreateStartedActor(string sessionId)
    {
        var actor = new SessionActor(sessionId, new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
        }, new StubGameModule());
        actor.Start();
        return actor;
    }

    [Fact]
    public async Task DisposeAsync_SecondCallIsNoOp()
    {
        // The split engine's registry is disposed from EngineIpcService.StopAsync,
        // and BackgroundService.DisposeAsync calls StopAsync again when the
        // execute task has not finished - so a second dispose is legal. It used
        // to blow up with ObjectDisposedException on the swept-then-disposed
        // sweep CTS.
        var registry = CreateRegistry();

        await registry.DisposeAsync();
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task CapExceeded_EvictsIdlestFirst()
    {
        await using var registry = CreateRegistry(maxSessions: 2);

        registry.GetOrCreate("older", nowMs: 1_000);
        registry.GetOrCreate("newer", nowMs: 60_000);

        var freshest = registry.GetOrCreate("freshest", nowMs: 120_000);

        Assert.Equal(2, registry.Count);
        Assert.False(registry.TryGet("older", out _));
        Assert.True(registry.TryGet("newer", out _));
        Assert.True(registry.TryGet("freshest", out _));
        await freshest.DisposeAsync();
    }

    [Fact]
    public async Task SweepIdle_RemovesOnlyClientlessIdle()
    {
        var withClients = new HashSet<string> { "active" };
        await using var registry = CreateRegistry(idleEvictAfterMs: 100, withClients: withClients);

        registry.GetOrCreate("idle", nowMs: 1_000);
        registry.GetOrCreate("active", nowMs: 1_000);

        var swept = await registry.SweepIdleAsync(nowMs: 1_000 + 700_000);

        Assert.Equal(1, swept);
        Assert.Equal(1, registry.Count);
        Assert.False(registry.TryGet("idle", out _));
        Assert.True(registry.TryGet("active", out _));
    }

    [Fact]
    public async Task GetOrCreate_HitRefreshesLastSeen()
    {
        await using var registry = CreateRegistry(idleEvictAfterMs: 100);

        var first = registry.GetOrCreate("s", nowMs: 1_000);
        registry.GetOrCreate("s", nowMs: 500_000);

        var swept = await registry.SweepIdleAsync(nowMs: 500_000 + 50);

        Assert.Equal(0, swept);
        Assert.True(registry.TryGet("s", out var again));
        Assert.Same(first, again);
    }
}
