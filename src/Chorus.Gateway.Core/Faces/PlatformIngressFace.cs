using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Gateway.Core.Actors;
using Chorus.Protocol.Models;
using Microsoft.AspNetCore.Http;

namespace Chorus.Gateway.Core.Faces;

/// <summary>
/// The platform-ingress listener face shared by the monolith and the gateway:
/// rate limit, body cap, timeout, per-platform verification, faction mapping
/// and dedup, then ONE Tell through <see cref="ISessionActorRouter"/>. The
/// only host-specific pieces are the router behind the seam and the optional
/// match aggregation (monolith exposes <see cref="MatchRegistry"/> under
/// /matches; the split gateway passes null).
///
/// Tell outcomes collapse to one retry posture: not-delivered (Rejected lane
/// full / Unavailable transport down) rolls back the dedup claim and answers
/// 503 so platform retries are fresh deliveries.
/// </summary>
public sealed class PlatformIngressFace
{
    private readonly IPlatformEventVerifier _verifier;
    private readonly IEnumerable<IPlatformEventMapper> _mappers;
    private readonly IAudienceFactionRule _factionRule;
    private readonly EventDeduplicator _dedup;
    private readonly IngressFaceGuard _faceGuard;
    private readonly FaceCounters _faces;
    private readonly ISessionActorRouter _actors;
    private readonly MatchRegistry? _matches;

    public PlatformIngressFace(
        IPlatformEventVerifier verifier,
        IEnumerable<IPlatformEventMapper> mappers,
        IAudienceFactionRule factionRule,
        EventDeduplicator dedup,
        IngressFaceGuard faceGuard,
        FaceCounters faces,
        ISessionActorRouter actors,
        MatchRegistry? matches = null)
    {
        _verifier = verifier;
        _mappers = mappers;
        _factionRule = factionRule;
        _dedup = dedup;
        _faceGuard = faceGuard;
        _faces = faces;
        _actors = actors;
        _matches = matches;
    }

    public async Task<IResult> HandleAsync(string platform, HttpContext context)
    {
        var sourceKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_faceGuard.AllowRequest(sourceKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            _faces.IngressRateLimited();
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        using var requestCts = _faceGuard.CreateRequestCancellationToken(context.RequestAborted);
        try
        {
            var body = await _faceGuard.ReadBodyAsync(context.Request.Body, requestCts.Token);
            if (!body.Ok)
            {
                _faces.IngressTooLarge();
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var raw = body.Body!;

            var headers = context.Request.Headers.ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            if (!_verifier.Verify(platform, raw, headers))
            {
                _faces.IngressUnverified();
                return Results.Unauthorized();
            }

            var ingestTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            AudienceEvent? ev = null;
            foreach (var mapper in _mappers)
            {
                ev = mapper.TryMap(platform, raw, ingestTimeMs);
                if (ev != null)
                {
                    break;
                }
            }

            if (ev == null)
            {
                _faces.IngressUnmapped();
                return Results.BadRequest(new { error = "unmapped_event", platform });
            }

            ev = _factionRule.Apply(ev);

            if (!_dedup.TryReserve(ev.Platform, ev.EventId, out var lease))
            {
                _faces.IngressDuplicates();
                return Results.Ok(new { ok = true, duplicated = true, sessionId = ev.SessionId, eventId = ev.EventId });
            }

            _matches?.Track(ev);

            var actorRef = _actors.Resolve(ev.SessionId);
            var outcome = await actorRef.TellAsync(ev, requestCts.Token);
            if (outcome != SessionTellOutcome.Accepted)
            {
                // Not delivered: release the claim so the platform retry is
                // treated as a fresh delivery (Failed), never misremembered as
                // a duplicate. Rejected (lane full) also counts as a face
                // timeout, mirroring the monolith's backpressure counter.
                _dedup.Rollback(lease);
                if (outcome == SessionTellOutcome.Rejected)
                {
                    _faces.IngressTimedOut();
                }
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            _dedup.Commit(lease);
            _faces.IngressAccepted();

            // The tick rides along only when the topology knows it (monolith);
            // the split gateway's anonymous shape has no tick field.
            return actorRef.TickId is long tick
                ? Results.Ok(new { ok = true, sessionId = ev.SessionId, eventId = ev.EventId, tick })
                : Results.Ok(new { ok = true, sessionId = ev.SessionId, eventId = ev.EventId });
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            _faces.IngressTimedOut();
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
