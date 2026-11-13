using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Protocol.Models;
using Chorus.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Chorus.PlatformGateway;

/// <summary>
/// The public webhook face (architecture 6.1's gateway half): one platform callback becomes
/// one durable data-plane fact. Verification and mapping are the mounted modules' work
/// (FIX-INPUT-08 assembly) — the face never parses a platform payload itself, it only moves
/// the verified bytes to the mapper and the mapped event to the routed push
/// (FIX-INPUT-02/05) or, when no route serves the event, the push cannot land durably, or
/// the room's delivery stands closed (FIX-END-07), to
/// <see cref="PlatformIngest.IngestBatch"/>. Whichever leg answers, every receipt the caller
/// sees reflects a committed fact, never an in-memory acceptance. Attribution is the data
/// plane's (canonical live session key, platform-strict): an event for a platform no
/// deployment can mount yet waits in the persistent inbox instead of being dropped.
/// </summary>
public sealed class PlatformIngressEndpoint(
    IPlatformEventVerifier? verifier,
    IEnumerable<IPlatformEventMapper> mappers,
    PlatformIngest.PlatformIngestClient ingest,
    IRoomRoutingDirectory? routing = null,
    Func<string, Grpc.Net.Client.GrpcChannel>? gameChannelFactory = null,
    Func<Grpc.Net.Client.GrpcChannel, RoomEvents.RoomEventsClient>? roomEventsFactory = null,
    TimeSpan[]? pushRetryBackoffs = null,
    StopDeliveryRecord? stopDeliveries = null,
    RoundInput.RoundInputClient? routeEvents = null,
    IngressCounters? counters = null)
{
    /// <summary>The face's own counters (FIX-OBS-10): every disposition this method answers
    /// bumps exactly one family, and the reporter carries the cumulative snapshot to the
    /// control plane. A caller that supplies none still gets counting — the instance-local
    /// counters just stay instance-local.</summary>
    private readonly IngressCounters _counters = counters ?? new IngressCounters();

    /// <summary>The platform application the callback belongs to; per-app callbacks are how
    /// every platform console routes webhooks, so the face asks for it explicitly.</summary>
    public const string AppIdHeader = "X-Chorus-App-Id";

    /// <summary>The pushed-event disposition contract (FIX-INPUT-01): the five answers a
    /// <c>RoomEvents.DeliverEvent</c> can give. The face only distinguishes the durable
    /// outcomes from the retryable refusals — the strings themselves are the wire contract.</summary>
    public static class PushDisposition
    {
        public const string DurableAccepted = "DurableAccepted";
        public const string Duplicate = "Duplicate";
        public const string StaleOwner = "StaleOwner";
        public const string Overloaded = "Overloaded";
        public const string OutcomeUnknown = "OutcomeUnknown";
    }

    /// <summary>The body cap of the old line's guard: one webhook is one event envelope,
    /// never a bulk feed.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>The per-attempt deadline of a routed push (FIX-INPUT-05). Attempts are bounded
    /// and the schedule below fits inside one webhook's lifetime, so the caller's own retry of
    /// the callback replays the same event id against a durable answer.</summary>
    internal static readonly TimeSpan PushAttemptDeadline = TimeSpan.FromSeconds(5);

    /// <summary>The backoff between push attempts: three attempts total, a quarter second then
    /// a second apart. StaleOwner and an unreachable owner each refresh the directory before
    /// the next attempt; Overloaded and OutcomeUnknown only back off — the retry of the same
    /// event id is the query that decides between a duplicate and a fresh accept.</summary>
    internal static readonly TimeSpan[] DefaultPushRetryBackoffs =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];

    // The stored payload must be a pure function of the verified platform body: the data
    // plane answers a platform retry as a duplicate by comparing payload hashes, so the
    // face's receive clock — delivery metadata, not event content — never enters the
    // payload it hashes. Ordering downstream is the delivery cursor's, not this field's.
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { ExcludeReceiveClock }
        }
    };

    private static void ExcludeReceiveClock(JsonTypeInfo type)
    {
        if (type.Type != typeof(AudienceEvent))
        {
            return;
        }
        var clock = type.Properties.FirstOrDefault(property => property.Name == "ingestTimeMs");
        if (clock is not null)
        {
            clock.ShouldSerialize = (_, _) => false;
        }
    }

    public async Task<IResult> HandleAsync(string platform, HttpContext context)
    {
        // One canonical spelling enters the pipeline (review 2026-11-13 m1): the routing
        // lookup, the stored batch platform, the operation ids, and the metrics labels all
        // see the same normalized identifier instead of the callback URL's casing.
        platform = PlatformNames.Normalize(platform) ?? string.Empty;

        if (verifier is null)
        {
            return Results.NotFound(new { error = "no_event_platforms", platform });
        }
        var appId = context.Request.Headers[AppIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(appId))
        {
            return Results.BadRequest(new { error = "missing_app_id", header = AppIdHeader });
        }

        var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            body.Write(buffer, 0, read);
            if (body.Length > MaxBodyBytes)
            {
                _counters.TooLarge();
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
        }
        var raw = Encoding.UTF8.GetString(body.ToArray());
        if (raw.Length == 0)
        {
            return Results.BadRequest(new { error = "empty_body", platform });
        }

        var headers = context.Request.Headers.ToDictionary(
            pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (!verifier.Verify(platform, raw, headers))
        {
            _counters.Unverified();
            return Results.Unauthorized();
        }

        // The app header is the caller's claim, not an identity (architecture review
        // C1): when the mounted credentials prove one, a disagreeing header is an
        // attribution spoof — it would re-scope dedup and the whole attribution chain
        // downstream. Refused here, counted in the same unverified family, and the
        // trusted value is never echoed back. A verifier that claims no identity
        // leaves the header's pre-existing meaning untouched.
        if (verifier is IPlatformTrustedAppIdSource trustedSource
            && trustedSource.GetTrustedAppId(platform) is { Length: > 0 } trustedAppId
            && !string.Equals(appId, trustedAppId, StringComparison.Ordinal))
        {
            _counters.Unverified();
            return Results.Json(new { error = "app_id_mismatch", platform },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var ingestTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AudienceEvent? mapped = null;
        foreach (var mapper in mappers)
        {
            mapped = mapper.TryMap(platform, raw, ingestTimeMs);
            if (mapped is not null)
            {
                break;
            }
        }
        if (mapped is null)
        {
            _counters.Unmapped();
            return Results.BadRequest(new { error = "unmapped_event", platform });
        }

        var payload = JsonSerializer.Serialize(mapped, PayloadOptions);

        // The routed push (FIX-INPUT-02) with its retry posture (FIX-INPUT-05): when the
        // control plane's routing snapshot names the room this live key belongs to — and that
        // room's owner serves an endpoint — the face pushes the event straight at the owning
        // game host. The push is the durable accept: DurableAccepted and Duplicate answer a
        // committed event, a malformed attribution is a 4xx that retrying cannot fix, and
        // every other answer retries the same event id a bounded number of times — StaleOwner
        // and an unreachable owner refresh the directory first, because the room may have
        // moved to an owner the cache has not seen. A retry that cannot land durably falls to
        // the persistent inbox below: an explicit pending record, never a drop, and the same
        // event id makes the two legs idempotent across each other.
        // Set when this event's room stands delivery-closed (FIX-END-07): the ingest leg
        // below is its durable home, and its committed receipt asks for one more drain pass
        // so the closed round's late arrivals reach a terminal disposition too.
        string? closedRoom = null;
        if (routing is not null && gameChannelFactory is not null)
        {
            var backoffs = pushRetryBackoffs ?? DefaultPushRetryBackoffs;
            for (var attempt = 0; ; attempt++)
            {
                if (!routing.TryResolve(platform, mapped.SessionId, out var route))
                    break;
                // FIX-END-07: a room whose delivery this gateway closed at the command's
                // version stops being a push target — no new pushes for the old round is the
                // closure's own meaning. The event falls to the persistent inbox, whose drain
                // pass disposes it terminally (OutOfRound) under the receiver's fence.
                if (stopDeliveries is not null && stopDeliveries.IsClosed(route.RoomId, route.RoundGeneration))
                {
                    closedRoom = route.RoomId;
                    break;
                }
                var push = new DeliverRoomEventRequest
                {
                    TenantId = route.TenantId,
                    GameId = route.GameId,
                    RoomId = route.RoomId,
                    OwnerInstanceId = route.OwnerInstanceId,
                    Epoch = route.Epoch,
                    EventId = mapped.EventId,
                    Platform = platform,
                    AppId = appId,
                    BindingId = route.BindingId,
                    // FIX-END-06: the push states the round generation the installed snapshot
                    // claims for the binding; the receiver checks the claim against the binding
                    // row, so this route's own staleness is the first thing admission hears.
                    RoundGeneration = route.RoundGeneration,
                    PayloadJson = payload
                };
                DeliverRoomEventReply? delivered = null;
                var staleDirectory = false;
                try
                {
                    using var channel = gameChannelFactory(route.Endpoint);
                    var client = roomEventsFactory is not null
                        ? roomEventsFactory(channel)
                        : new RoomEvents.RoomEventsClient(channel);
                    delivered = await client.DeliverEventAsync(push,
                        deadline: DateTime.UtcNow.Add(PushAttemptDeadline),
                        cancellationToken: context.RequestAborted);
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
                {
                    // The receiver refused the push. A room whose closure now stands — the
                    // command landed mid-flight, between this resolve and the receiver's own
                    // fence (FIX-END-06) — falls to the persistent inbox: a 400 would leave
                    // the event without any durable trace. Anything else the receiver refused
                    // (a malformed push) keeps its 400: retrying cannot fix it.
                    if (stopDeliveries is null)
                        return Results.BadRequest(new { error = "rejected_push", platform, detail = error.Status.Detail });
                    routing.TryResolve(platform, mapped.SessionId, out var fenced);
                    if (fenced is not null && !stopDeliveries.IsClosed(fenced.RoomId, fenced.RoundGeneration))
                        return Results.BadRequest(new { error = "rejected_push", platform, detail = error.Status.Detail });
                    closedRoom = fenced?.RoomId;
                    break;
                }
                catch (RpcException)
                {
                    // Not committed and the owner is unreachable: the directory may name a
                    // dead owner, so the next attempt asks the directory again.
                    staleDirectory = true;
                }
                if (delivered is not null)
                {
                    if (delivered.Disposition is PushDisposition.DurableAccepted
                        or PushDisposition.Duplicate)
                    {
                        if (delivered.Disposition == PushDisposition.Duplicate)
                            _counters.Duplicates();
                        else
                            _counters.Accepted();
                        // The pair the drain later routes and the watermark the ack reports:
                        // what this process durably observed for the room, nothing more.
                        stopDeliveries?.ObserveApp(platform, appId);
                        if (delivered.Sequence > 0)
                            stopDeliveries?.RecordCutoff(route.RoomId, delivered.Sequence);
                        return Results.Ok(new
                        {
                            ok = true,
                            platform,
                            receipts = new[]
                            {
                                new
                                {
                                    eventId = mapped.EventId,
                                    disposition = delivered.Disposition,
                                    bindingId = route.BindingId,
                                    duplicate = delivered.Disposition == PushDisposition.Duplicate
                                }
                            }
                        });
                    }
                    if (delivered.Disposition == PushDisposition.StaleOwner)
                    {
                        // The room's authority moved on: refresh and re-resolve so the next
                        // attempt names the new owner, not the refused one.
                        staleDirectory = true;
                    }
                    // Overloaded and OutcomeUnknown committed nothing — a bounded retry of
                    // the same event id is the query that decides duplicate versus accept.
                }
                if (attempt >= backoffs.Length)
                    break;
                if (staleDirectory)
                    await routing.RefreshAsync(context.RequestAborted);
                await Task.Delay(backoffs[attempt], context.RequestAborted);
            }
        }

        // No valid attribution, or the push could not land durably in bounded retries: the
        // event waits durably in the persistent inbox — an explicit pending, never a drop.
        // The routed leg writes the same source dedup key, so a platform retry of this
        // callback replays as a duplicate of whatever the push leg already committed.
        var request = new IngestBatchRequest
        {
            Platform = platform,
            AppId = appId,
            // One HTTP callback is one data-plane operation; the platform's own retry of the
            // same callback is a fresh operation whose event ids the store answers as
            // duplicates with their original attribution.
            OperationId = $"ingress:{platform}:{Guid.NewGuid():N}"
        };
        request.Events.Add(new IngestEvent
        {
            SourceDedupKey = mapped.EventId,
            Durability = "Durable",
            CanonicalRoomKey = mapped.SessionId,
            PayloadJson = payload
        });

        IngestBatchReply committed;
        try
        {
            committed = await ingest.IngestBatchAsync(request, null,
                DateTime.UtcNow.AddSeconds(10), context.RequestAborted);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            return Results.BadRequest(new { error = "rejected_batch", platform, detail = error.Status.Detail });
        }
        catch (RpcException)
        {
            _counters.TimedOut();
            // Not committed: the platform's retry must be a fresh delivery, and the face says
            // so with the one retry posture that never acknowledges a lost event.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        // The durable answers the batch committed (FIX-OBS-10): a duplicate receipt counts
        // as the duplicate it is, any other committed receipt is an accept — the persistent
        // inbox's attribution is as durable an acceptance as the push leg's own.
        foreach (var receipt in committed.Receipts)
        {
            if (receipt.Duplicate) _counters.Duplicates();
            else _counters.Accepted();
        }

        // FIX-SYNC-07: the ingest leg only attributes — nothing in production routes a live
        // room's attributed events after it (the drain pass is a closed room's work). Without
        // this pass a paused room's buffered-input gate would never hear its events. The pass
        // is paused-only: a live room's attributed event keeps waiting for its owner (the
        // push leg) or a closed round's drain pass, so the trigger never changes a live
        // room's dispositions — only a paused room's. One bounded routing attempt right
        // here; a failure costs nothing — the event stays durably Attributed and a later
        // webhook replays the same pass.
        if (routeEvents is not null && closedRoom is null)
        {
            try
            {
                await routeEvents.RouteEventsAsync(new RouteEventsRequest
                {
                    Platform = platform,
                    AppId = appId,
                    OperationId = $"ingress-route:{platform}:{Guid.NewGuid():N}",
                    MaxEvents = 64,
                    PausedOnly = true
                }, deadline: DateTime.UtcNow.AddSeconds(10),
                cancellationToken: context.RequestAborted);
            }
            catch (RpcException)
            {
                // The events are durably Attributed either way; the next webhook retires the pass.
            }
        }

        var receipts = committed.Receipts.Select(receipt => new
        {
            eventId = mapped.EventId,
            disposition = receipt.Disposition,
            bindingId = receipt.BindingId,
            duplicate = receipt.Duplicate
        });
        // The pair this process durably landed under — the enumeration a later drain pass
        // routes — and, for a closed round's late arrival, the request for one more drain
        // pass: its terminal disposition (OutOfRound) is the closure's own work (FIX-END-07).
        stopDeliveries?.ObserveApp(platform, appId);
        if (closedRoom is not null)
            stopDeliveries?.RequestDrain(closedRoom);
        return Results.Ok(new { ok = true, platform, receipts });
    }
}
