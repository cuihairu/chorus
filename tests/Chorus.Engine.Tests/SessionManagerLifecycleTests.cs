using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol.Models;
using Chorus.Server;

namespace Chorus.Engine.Tests;

/// <summary>
/// Review stage-2 acceptance (Session 生命周期): the session cap evicts the
/// least-recently-seen session to keep new streams alive, and idle eviction
/// only reaps client-less sessions.
/// </summary>
public sealed class SessionManagerLifecycleTests
{
    private static SessionManager CreateManager(int maxSessions, int idleEvictAfterMs)
        => new(new StubGameModuleFactory(), new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
            MaxSessions = maxSessions,
            CloseClientlessIdleRooms = true,
            IdleEvictAfterMs = idleEvictAfterMs,
        });

    [Fact]
    public async Task CapExceeded_RejectsNewSessionWithoutDestroyingExistingState()
    {
        await using var manager = CreateManager(maxSessions: 2, idleEvictAfterMs: 600_000);
        var first = manager.GetOrCreate("s1");
        manager.GetOrCreate("s2");
        Assert.Throws<SessionCapacityException>(() => manager.GetOrCreate("s3"));
        Assert.Equal(2, manager.Count);
        Assert.Same(first, manager.GetOrCreate("s1"));
        Assert.Equal(1, manager.CapacityRejections);
    }

    [Fact]
    public async Task SweepIdle_RemovesOnlyClientlessIdleSessions()
    {
        // 600s window keeps the constructor-started background sweeper at its
        // 60s clamp so it can never fire mid-test and steal the eviction.
        await using var manager = CreateManager(maxSessions: 100, idleEvictAfterMs: 600_000);

        var idle = manager.GetOrCreate("idle");
        var active = manager.GetOrCreate("active");
        await active.AddClientAsync(new NullClient("c1"));

        // Let the actor process the AddClient so diagnostics see the client.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (active.Diagnostics.Clients.Count == 0 && sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
        }

        var swept = await manager.SweepIdleAsync(nowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 700_000);

        Assert.Equal(1, swept);
        Assert.Equal(1, manager.Count);
        Assert.DoesNotContain(manager.GetDiagnostics(), d => d.SessionId == "idle");
        Assert.Contains(manager.GetDiagnostics(), d => d.SessionId == "active");

        await idle.StopAsync();
    }

    [Fact]
    public async Task SweepIdle_RecentSession_IsKept()
    {
        await using var manager = CreateManager(maxSessions: 100, idleEvictAfterMs: 600_000);

        manager.GetOrCreate("fresh");
        var swept = await manager.SweepIdleAsync(nowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0, swept);
        Assert.Equal(1, manager.Count);
    }

    private sealed class StubGameModuleFactory : IGameModuleFactory
    {
        public IGameModule Create(string sessionId) => new StubGameModule();
    }

    private sealed class StubGameModule : IGameModule
    {
        public string Name => "stub-lifecycle";

        public object GetStateSnapshot() => new { };

        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events)
        {
        }

        public void RestoreState(long version, System.Text.Json.JsonElement state)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class NullClient : ISessionClient
    {
        public NullClient(string clientId) => ClientId = clientId;

        public string ClientId { get; }

        public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
