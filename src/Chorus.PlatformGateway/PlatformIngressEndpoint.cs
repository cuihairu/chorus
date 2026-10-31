using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chorus.Adapters.Abstractions;
using Chorus.Protocol.Models;
using Chorus.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Chorus.PlatformGateway;

/// <summary>
/// The public webhook face (architecture 6.1's gateway half): one platform callback becomes
/// one durable data-plane batch. Verification and mapping are the mounted modules' work
/// (FIX-INPUT-08 assembly) — the face never parses a platform payload itself, it only moves
/// the verified bytes to the mapper and the mapped event to
/// <see cref="PlatformIngest.IngestBatch"/>, whose reply is the face's reply: every receipt
/// the caller sees reflects a committed batch, never an in-memory acceptance. Attribution is
/// the data plane's (canonical live session key, platform-strict): an event for a platform
/// no deployment can mount yet waits in the persistent inbox instead of being dropped.
/// </summary>
public sealed class PlatformIngressEndpoint(
    IPlatformEventVerifier? verifier,
    IEnumerable<IPlatformEventMapper> mappers,
    PlatformIngest.PlatformIngestClient ingest,
    GatewayRoutingMonitor? routing = null,
    Func<string, Grpc.Net.Client.GrpcChannel>? gameChannelFactory = null)
{
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
            return Results.Unauthorized();
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
            return Results.BadRequest(new { error = "unmapped_event", platform });
        }

        var payload = JsonSerializer.Serialize(mapped, PayloadOptions);

        // The routed push (FIX-INPUT-02): when the control plane's routing snapshot names the
        // room this live key belongs to — and that room's owner serves an endpoint — the face
        // pushes the event straight at the owning game host. The push is the durable accept:
        // DurableAccepted and Duplicate answer a committed event, everything else is a refusal
        // the caller retries, and a malformed attribution is a 4xx that retrying cannot fix.
        if (routing is not null && gameChannelFactory is not null
            && routing.TryResolve(platform, mapped.SessionId, out var route))
        {
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
                PayloadJson = payload
            };
            DeliverRoomEventReply delivered;
            try
            {
                using var channel = gameChannelFactory(route.Endpoint);
                delivered = await new RoomEvents.RoomEventsClient(channel).DeliverEventAsync(push,
                    deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: context.RequestAborted);
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
            {
                return Results.BadRequest(new { error = "rejected_push", platform, detail = error.Status.Detail });
            }
            catch (RpcException)
            {
                // Not committed and the host is unreachable: retry the same event id.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            if (delivered.Disposition is not (PushDisposition.DurableAccepted or PushDisposition.Duplicate))
            {
                // StaleOwner, Overloaded, OutcomeUnknown: nothing committed — a retry of the
                // same event id is the contract, and a 503 says so without acking the loss.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
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

        // No valid attribution: the callback's live key names no routed room, so the event
        // waits durably in the persistent inbox — an explicit pending, never a drop. The
        // durable attribution leg is the migration baseline and answers this case.
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
            // Not committed: the platform's retry must be a fresh delivery, and the face says
            // so with the one retry posture that never acknowledges a lost event.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var receipts = committed.Receipts.Select(receipt => new
        {
            eventId = mapped.EventId,
            disposition = receipt.Disposition,
            bindingId = receipt.BindingId,
            duplicate = receipt.Duplicate
        });
        return Results.Ok(new { ok = true, platform, receipts });
    }
}
