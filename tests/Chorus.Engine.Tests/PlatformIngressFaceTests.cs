using System.Text;
using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Gateway.Core.Actors;
using Chorus.Gateway.Core.Faces;
using Chorus.Protocol.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chorus.Engine.Tests;

/// <summary>
/// Tell-seam acceptance for the shared platform-ingress face (todo §3.5
/// ActorRef/Tell). The face is ONE implementation for the monolith and the
/// split gateway; only the <see cref="ISessionActorRouter"/> behind it
/// differs. A stub router scripts each Tell outcome so the delivery contract
/// is pinned without an engine: Accepted commits the dedup claim and returns
/// 200 (tick field only when the topology knows it), Rejected/Unavailable
/// roll the claim back and return 503 so platform retries are fresh
/// deliveries, and gate failures never touch the seam.
/// </summary>
public sealed class PlatformIngressFaceTests
{
    private sealed class StubVerifier(bool ok = true) : IPlatformEventVerifier
    {
        public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers) => ok;
    }

    private sealed class StubMapper(string eventId = "e1", string sessionId = "s-face", string? matchId = "m1") : IPlatformEventMapper
    {
        public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs)
            => new AudienceEvent(
                EventId: eventId,
                Platform: platform,
                SessionId: sessionId,
                UserId: "u1",
                Kind: AudienceEventKind.Comment,
                IngestTimeMs: ingestTimeMs,
                MatchId: matchId);
    }

    private sealed class PassThroughRule : IAudienceFactionRule
    {
        public AudienceEvent Apply(AudienceEvent e) => e;
    }

    private sealed class ScriptedActorRef : ISessionActorRef
    {
        private readonly Queue<SessionTellOutcome> _script;

        public ScriptedActorRef(string sessionId, long? tickId, IEnumerable<SessionTellOutcome> script)
        {
            SessionId = sessionId;
            TickId = tickId;
            _script = new Queue<SessionTellOutcome>(script);
        }

        public string SessionId { get; }

        public long? TickId { get; }

        public int Tells { get; private set; }

        public async ValueTask<SessionTellOutcome> TellAsync(AudienceEvent e, CancellationToken cancellationToken)
        {
            Tells++;
            await Task.CompletedTask;
            return _script.Dequeue();
        }
    }

    private sealed class ScriptedRouter(ISessionActorRef actorRef) : ISessionActorRouter
    {
        public ISessionActorRef Resolve(string sessionId) => actorRef;
    }

    /// <summary>Script steps are SessionTellOutcome values or Exceptions.</summary>
    private sealed class ScriptedFaultyActorRef(string sessionId, params object[] script) : ISessionActorRef
    {
        private readonly Queue<object> _script = new(script);

        public string SessionId { get; } = sessionId;

        public long? TickId => null;

        public int Tells { get; private set; }

        public ValueTask<SessionTellOutcome> TellAsync(AudienceEvent e, CancellationToken cancellationToken)
        {
            Tells++;
            return _script.Dequeue() switch
            {
                Exception ex => ValueTask.FromException<SessionTellOutcome>(ex),
                SessionTellOutcome outcome => ValueTask.FromResult(outcome),
                _ => throw new InvalidOperationException("script step must be outcome or exception"),
            };
        }
    }

    private static PlatformIngressFace CreateFace(
        ISessionActorRef actorRef,
        MatchRegistry? matches = null,
        bool verifierOk = true,
        bool mapEvent = true)
    {
        return new PlatformIngressFace(
            new StubVerifier(verifierOk),
            new IPlatformEventMapper[] { mapEvent ? new StubMapper() : new NullMapper() },
            new PassThroughRule(),
            new EventDeduplicator(TimeSpan.FromMinutes(10)),
            new IngressFaceGuard(new IngressFaceOptions()),
            new FaceCounters(),
            new ScriptedRouter(actorRef),
            matches);
    }

    private sealed class NullMapper : IPlatformEventMapper
    {
        public AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs) => null;
    }

    private static class MinimalHost
    {
        public static readonly IServiceProvider Services = WebApplication.Create().Services;
    }

    private static async Task<(int Status, string Body)> SendAsync(PlatformIngressFace face, string body = "{}")
    {
        var context = new DefaultHttpContext
        {
            // Results.Ok/StatusCode executors resolve ASP.NET services off the
            // context; a bare DefaultHttpContext has none, so hand over a
            // minimal host's provider (no server is started).
            RequestServices = MinimalHost.Services,
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        var result = await face.HandleAsync("douyin", context);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Accepted_UnknownTick_OmitsTickField()
    {
        var actor = new ScriptedActorRef("s-face", tickId: null, new[] { SessionTellOutcome.Accepted });
        var face = CreateFace(actor);

        var (status, body) = await SendAsync(face);

        Assert.Equal(200, status);
        Assert.Contains("\"ok\":true", body);
        Assert.Contains("s-face", body);
        Assert.DoesNotContain("tick", body);
        Assert.Equal(1, actor.Tells);
    }

    [Fact]
    public async Task Accepted_KnownTick_IncludedInResponse()
    {
        var actor = new ScriptedActorRef("s-face", tickId: 7, new[] { SessionTellOutcome.Accepted });
        var face = CreateFace(actor);

        var (status, body) = await SendAsync(face);

        Assert.Equal(200, status);
        Assert.Contains("\"tick\":7", body);
    }

    [Fact]
    public async Task Accepted_TracksMatch_WhenRegistryPresent()
    {
        var actor = new ScriptedActorRef("s-face", tickId: null, new[] { SessionTellOutcome.Accepted });
        var matches = new MatchRegistry();
        var face = CreateFace(actor, matches: matches);

        var (status, _) = await SendAsync(face);

        Assert.Equal(200, status);
        Assert.Equal(1, matches.GetDiagnostics().Single().TotalEvents);
    }

    [Fact]
    public async Task Rejected_Returns503_RollsBackDedupClaim()
    {
        var actor = new ScriptedActorRef("s-face", tickId: null, new[] { SessionTellOutcome.Rejected, SessionTellOutcome.Accepted });
        var face = CreateFace(actor);

        var (first, _) = await SendAsync(face);
        var (retry, retryBody) = await SendAsync(face);

        Assert.Equal(503, first);
        Assert.Equal(2, actor.Tells);
        // The rollback is the point: the platform retry must be a fresh
        // delivery, never misremembered as a duplicate.
        Assert.Equal(200, retry);
        Assert.DoesNotContain("duplicated", retryBody);
    }

    [Fact]
    public async Task Unavailable_Returns503_RollsBackDedupClaim()
    {
        var actor = new ScriptedActorRef("s-face", tickId: null, new[] { SessionTellOutcome.Unavailable, SessionTellOutcome.Accepted });
        var face = CreateFace(actor);

        var (first, _) = await SendAsync(face);
        var (retry, retryBody) = await SendAsync(face);

        Assert.Equal(503, first);
        Assert.Equal(2, actor.Tells);
        Assert.Equal(200, retry);
        Assert.DoesNotContain("duplicated", retryBody);
    }

    [Fact]
    public async Task DuplicateEvent_ShortCircuits_BeforeTell()
    {
        var actor = new ScriptedActorRef("s-face", tickId: null, new[] { SessionTellOutcome.Accepted });
        var face = CreateFace(actor);

        var (first, _) = await SendAsync(face);
        var (second, secondBody) = await SendAsync(face);

        Assert.Equal(200, first);
        Assert.Equal(200, second);
        Assert.Contains("\"duplicated\":true", secondBody);
        Assert.Equal(1, actor.Tells);
    }

    [Fact]
    public async Task UnmappedEvent_Returns400_NeverTells()
    {
        var actor = new ScriptedActorRef("s-face", tickId: null, Array.Empty<SessionTellOutcome>());
        var face = CreateFace(actor, mapEvent: false);

        var (status, body) = await SendAsync(face);

        Assert.Equal(400, status);
        Assert.Contains("unmapped_event", body);
        Assert.Equal(0, actor.Tells);
    }

    [Fact]
    public async Task UnverifiedCallback_Returns401_NeverTells()
    {
        var actor = new ScriptedActorRef("s-face", tickId: null, Array.Empty<SessionTellOutcome>());
        var face = CreateFace(actor, verifierOk: false);

        var (status, _) = await SendAsync(face);

        Assert.Equal(401, status);
        Assert.Equal(0, actor.Tells);
    }

    [Fact]
    public async Task TellTimesOut_Returns504_RollsBackDedupClaim()
    {
        // Tell cancelled by the engine (not by this HTTP request): the face
        // must answer 504 so the platform retries fresh, and the retry is a
        // new delivery rather than a remembered duplicate.
        var actor = new ScriptedFaultyActorRef("s-face", new TaskCanceledException(), SessionTellOutcome.Accepted);
        var face = CreateFace(actor);

        var (first, _) = await SendAsync(face);
        var (retry, retryBody) = await SendAsync(face);

        Assert.Equal(504, first);
        Assert.Equal(2, actor.Tells);
        Assert.Equal(200, retry);
        Assert.DoesNotContain("duplicated", retryBody);
    }

    [Fact]
    public async Task TellFaults_Returns503_RollsBackDedupClaim()
    {
        // Router/tell blew up outright: 503 + rollback, same retry contract.
        var actor = new ScriptedFaultyActorRef("s-face", new InvalidOperationException("router broke"), SessionTellOutcome.Accepted);
        var face = CreateFace(actor);

        var (first, _) = await SendAsync(face);
        var (retry, retryBody) = await SendAsync(face);

        Assert.Equal(503, first);
        Assert.Equal(2, actor.Tells);
        Assert.Equal(200, retry);
        Assert.DoesNotContain("duplicated", retryBody);
    }
}
