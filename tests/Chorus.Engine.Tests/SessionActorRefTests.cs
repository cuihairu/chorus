using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Gateway.Core.Actors;
using Chorus.Protocol.Models;
using Chorus.Server;

namespace Chorus.Engine.Tests;

/// <summary>
/// ActorRef/Tell acceptance (todo §3.5, shared seam): the monolith's
/// <see cref="InProcSessionActorRouter"/> is the in-process routing table
/// (sessionId → actor, backed by the shared <c>SessionRegistry</c>), and its
/// refs deliver audience events with explicit Tell outcomes — Accepted into
/// the inbox, Rejected when the bounded lane is full.
/// </summary>
public sealed class SessionActorRefTests
{
    private sealed class StubGameModule : IGameModule
    {
        public string Name => "stub-actorref";
        public object GetStateSnapshot() => new { };
        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events) { }
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }

        public void Dispose() { }
    }

    private sealed class StubGameModuleFactory : IGameModuleFactory
    {
        public IGameModule Create(string sessionId) => new StubGameModule();
    }

    private static AudienceEvent CommentEvent(string sessionId, string eventId) => new(
        EventId: eventId,
        Platform: "douyin",
        SessionId: sessionId,
        UserId: "u1",
        Kind: AudienceEventKind.Comment,
        IngestTimeMs: 0);

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Tell_Accepted_ReachesTheActorInbox()
    {
        await using var sessions = new SessionManager(new StubGameModuleFactory(), new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
        });
        var router = new InProcSessionActorRouter(sessions);

        var outcome = await router.Resolve("s-ref").TellAsync(CommentEvent("s-ref", "e1"), CancellationToken.None);

        Assert.Equal(SessionTellOutcome.Accepted, outcome);
        await WaitForAsync(() => sessions.GetDiagnostics().Single(d => d.SessionId == "s-ref").TotalIngestedEvents == 1);
    }

    [Fact]
    public async Task Router_ResolvesSameActor_PerSession()
    {
        await using var sessions = new SessionManager(new StubGameModuleFactory(), new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
        });
        var router = new InProcSessionActorRouter(sessions);

        await router.Resolve("s-ref").TellAsync(CommentEvent("s-ref", "e1"), CancellationToken.None);
        await router.Resolve("s-ref").TellAsync(CommentEvent("s-ref", "e2"), CancellationToken.None);

        // Both refs land in the same session's inbox (one actor, two events).
        await WaitForAsync(() => sessions.GetDiagnostics().Single(d => d.SessionId == "s-ref").TotalIngestedEvents == 2);
        Assert.Single(sessions.GetDiagnostics());
    }

    [Fact]
    public async Task Tell_Rejected_WhenBoundedLaneFull()
    {
        // Unstarted actor: the lane is never drained, so capacity 1 pins the
        // Rejected outcome deterministically. Comment events drop immediately
        // (only gifts get the bounded wait).
        await using var actor = new SessionActor("s-lane", new SessionConfig
        {
            TickDurationMs = 600_000,
            SnapshotEveryTicks = 0,
            MaxPendingEvents = 1,
        }, new StubGameModule());
        var actorRef = new InProcSessionActorRef(actor);

        var first = await actorRef.TellAsync(CommentEvent("s-lane", "e1"), CancellationToken.None);
        var second = await actorRef.TellAsync(CommentEvent("s-lane", "e2"), CancellationToken.None);

        Assert.Equal(SessionTellOutcome.Accepted, first);
        Assert.Equal(SessionTellOutcome.Rejected, second);
    }

    [Fact]
    public async Task Ref_ExposesEngineTickId()
    {
        await using var sessions = new SessionManager(new StubGameModuleFactory(), new SessionConfig
        {
            TickDurationMs = 10,
            SnapshotEveryTicks = 0,
        });
        var router = new InProcSessionActorRouter(sessions);

        var actorRef = router.Resolve("s-ref");
        Assert.True(actorRef.TickId >= 0);
        await WaitForAsync(() => actorRef.TickId >= 1);
    }
}
