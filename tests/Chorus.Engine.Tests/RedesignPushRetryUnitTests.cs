using System.Text;
using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.PlatformGateway;
using Chorus.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static Chorus.Rpc.RoomEvents;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-05, the face's retry posture in unit pins: a push the stale owner picture
/// refused refreshes the directory and re-pushes to the new owner under the same event id;
/// a lost success reply retries the same event id and the duplicate ends it; a push that
/// cannot land durably inside its bounded retries falls to the persistent inbox — an
/// explicit pending record under the same dedup key, never a drop; a capacity refusal backs
/// off and retries. FIX-END-07 adds the closure posture: a room whose delivery stands
/// closed never pushes — late and mid-flight fenced arrivals alike fall to the inbox leg,
/// while malformed refusals keep their visible 400. The scripted doubles hold the whole
/// world between the face and the wire.
/// </summary>
public sealed class RedesignPushRetryUnitTests
{
    [Fact]
    public async Task AStaleOwnerRefreshesTheDirectoryAndRepushesToTheNewOwner()
    {
        var roomEvents = new ScriptedRoomEvents(
            Reply("StaleOwner"), Reply("DurableAccepted"));
        var directory = new ScriptedDirectory(
            Target("game-a", 1, "https://localhost:6001"),
            Target("game-b", 2, "https://localhost:6002"));
        var ingest = new ScriptedIngest();

        var response = await PostAsync(roomEvents, directory, ingest,
            """{"sessionId":"live-a","eventId":"retry-1","kind":"Gift","giftId":"g1","giftCount":1,"giftValue":10}""");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        // The first push named the stale owner; the refresh re-resolved the new one and the
        // same event id landed there — one webhook, one durable accept, no fallback leg.
        Assert.Equal(2, roomEvents.Seen.Count);
        Assert.Equal("game-a", roomEvents.Seen[0].OwnerInstanceId);
        Assert.Equal(1L, roomEvents.Seen[0].Epoch);
        Assert.Equal("game-b", roomEvents.Seen[1].OwnerInstanceId);
        Assert.Equal(2L, roomEvents.Seen[1].Epoch);
        Assert.All(roomEvents.Seen, push => Assert.Equal("retry-1", push.EventId));
        // The face forwards its snapshot's round-generation claim on every push (FIX-END-06)
        // — the receiver-side fence hears the picture the resolve answered from.
        Assert.All(roomEvents.Seen, push => Assert.Equal(7L, push.RoundGeneration));
        Assert.Equal(1, directory.Refreshes);
        var receipt = Assert.Single(response.Receipts);
        Assert.Equal("DurableAccepted", receipt.GetProperty("disposition").GetString());
        Assert.False(receipt.GetProperty("duplicate").GetBoolean());
        Assert.Equal(0, ingest.Calls);
    }

    [Fact]
    public async Task ALostSuccessReplyRetriesTheSameEventIdAndTheDuplicateEndsIt()
    {
        // The durable leg committed but its reply was lost — the retry of the same event id
        // is the query, and the duplicate it answers is the success the caller sees once.
        var roomEvents = new ScriptedRoomEvents(
            Reply("OutcomeUnknown"), Reply("Duplicate"));
        var directory = new ScriptedDirectory(Target("game-a", 1, "https://localhost:6001"), null!);
        var ingest = new ScriptedIngest();

        var response = await PostAsync(roomEvents, directory, ingest,
            """{"sessionId":"live-a","eventId":"retry-2","kind":"Gift","giftId":"g2","giftCount":1,"giftValue":20}""");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(2, roomEvents.Seen.Count);
        Assert.All(roomEvents.Seen, push => Assert.Equal("retry-2", push.EventId));
        var receipt = Assert.Single(response.Receipts);
        Assert.Equal("Duplicate", receipt.GetProperty("disposition").GetString());
        Assert.True(receipt.GetProperty("duplicate").GetBoolean());
        Assert.Equal(0, ingest.Calls);
    }

    [Fact]
    public async Task AnUnreachableOwnerExhaustsItsRetriesIntoThePendingRecord()
    {
        var roomEvents = new ScriptedRoomEvents(
            Unreachable(), Unreachable(), Unreachable());
        var directory = new ScriptedDirectory(Target("game-a", 1, "https://localhost:6001"), null!);
        var ingest = new ScriptedIngest();

        var response = await PostAsync(roomEvents, directory, ingest,
            """{"sessionId":"live-a","eventId":"retry-3","kind":"Gift","giftId":"g3","giftCount":1,"giftValue":30}""");

        // Three attempts is the whole budget; the event then waits durably in the persistent
        // inbox under the same dedup key the push leg would have written — never a drop and
        // never a second effect when a late retry of the push leg finds it committed.
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(3, roomEvents.Seen.Count);
        Assert.Equal(1, ingest.Calls);
        Assert.Equal("retry-3", ingest.LastDedupKey);
        var receipt = Assert.Single(response.Receipts);
        Assert.Equal("Durable", receipt.GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task AnOverloadedRoomIsRetriedAfterBackoff()
    {
        var roomEvents = new ScriptedRoomEvents(
            Reply("Overloaded"), Reply("Overloaded"), Reply("DurableAccepted"));
        var directory = new ScriptedDirectory(Target("game-a", 1, "https://localhost:6001"), null!);
        var ingest = new ScriptedIngest();

        var response = await PostAsync(roomEvents, directory, ingest,
            """{"sessionId":"live-a","eventId":"retry-4","kind":"Gift","giftId":"g4","giftCount":1,"giftValue":40}""");

        // Capacity is not topology: the directory is not refreshed, the same target is
        // retried after backoff until the room drains its seats.
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(3, roomEvents.Seen.Count);
        Assert.All(roomEvents.Seen, push => Assert.Equal("game-a", push.OwnerInstanceId));
        Assert.Equal(0, directory.Refreshes);
        Assert.Equal(0, ingest.Calls);
    }

    /// <summary>
    /// FIX-END-07: a room whose delivery this gateway closed stops being a push target —
    /// the closure is the gate before any push. The event falls to the persistent inbox
    /// under the same dedup key, its durable home until the drain disposes it terminally.
    /// </summary>
    [Fact]
    public async Task AClosedRoomFallsToTheDurableLegInsteadOfPushing()
    {
        // No scripted push outcome: any push attempt fails the fact loudly.
        var roomEvents = new ScriptedRoomEvents();
        var directory = new ScriptedDirectory(Target("game-a", 1, "https://localhost:6001"), null!);
        var ingest = new ScriptedIngest();
        var stopDeliveries = new StopDeliveryRecord();
        Assert.True(stopDeliveries.TryClose("tenant-a", "game-a", "room-retry", "end-op-retry",
            7, 99, out _, out _));

        var response = await PostAsync(roomEvents, directory, ingest,
            """{"sessionId":"live-a","eventId":"closed-1","kind":"Gift","giftId":"g5","giftCount":1,"giftValue":50}""",
            stopDeliveries);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(0, roomEvents.Seen.Count);
        Assert.Equal(1, ingest.Calls);
        Assert.Equal("closed-1", ingest.LastDedupKey);
        var receipt = Assert.Single(response.Receipts);
        Assert.Equal("Durable", receipt.GetProperty("disposition").GetString());
    }

    /// <summary>
    /// FIX-END-07's mid-flight window: the push resolved before the command landed and the
    /// receiver's fence (FIX-END-06) refused it after. A 400 would leave the event without
    /// any durable trace, so when the closure now stands the event falls to the persistent
    /// inbox instead — the only remaining no-trace path closed.
    /// </summary>
    [Fact]
    public async Task AFencedPushFallsBackToTheDurableLegWhenTheClosureStands()
    {
        var stopDeliveries = new StopDeliveryRecord();
        var roomEvents = new ScriptedRoomEvents(Fenced(() =>
            stopDeliveries.TryClose("tenant-a", "game-a", "room-retry", "end-op-retry", 7, 99,
                out _, out _)));
        var directory = new ScriptedDirectory(Target("game-a", 1, "https://localhost:6001"), null!);
        var ingest = new ScriptedIngest();

        var response = await PostAsync(roomEvents, directory, ingest,
            """{"sessionId":"live-a","eventId":"fenced-1","kind":"Gift","giftId":"g6","giftCount":1,"giftValue":60}""",
            stopDeliveries);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(1, roomEvents.Seen.Count);
        Assert.Equal(1, ingest.Calls);
        Assert.Equal("fenced-1", ingest.LastDedupKey);
        // The closed round's late arrival asked its closure for one more drain pass — its
        // terminal disposition is the closure's own work, never the platform's retry.
        Assert.True(stopDeliveries.DrainQueue.TryRead(out var drained));
        Assert.Equal("room-retry", drained);
    }

    /// <summary>
    /// The fall-through is scoped to the closure: a receiver refusal for any other reason —
    /// a malformed push — keeps its visible 400, because retrying cannot fix it and the
    /// inbox is not a trash can for broken requests.
    /// </summary>
    [Fact]
    public async Task AMalformedPushRefusalKeepsItsVisible400()
    {
        var roomEvents = new ScriptedRoomEvents(new RpcException(new Status(StatusCode.InvalidArgument,
            "the binding does not point at this room")));
        var directory = new ScriptedDirectory(Target("game-a", 1, "https://localhost:6001"), null!);
        var ingest = new ScriptedIngest();
        var stopDeliveries = new StopDeliveryRecord();
        stopDeliveries.ObserveApp("simulator", "app-1");

        var response = await PostAsync(roomEvents, directory, ingest,
            """{"sessionId":"live-a","eventId":"malformed-1","kind":"Gift","giftId":"g7","giftCount":1,"giftValue":70}""",
            stopDeliveries);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.Equal(1, roomEvents.Seen.Count);
        Assert.Equal(0, ingest.Calls);
    }

    private static DeliverRoomEventReply Reply(string disposition) => new() { Disposition = disposition };

    private static RpcException Unreachable() =>
        new(new Status(StatusCode.Unavailable, "the owner endpoint refused the connection"));

    /// <summary>The receiver's generation fence (FIX-END-06) as the push leg meets it. A
    /// factory, not an exception: the double runs it when the push dequeues the outcome —
    /// after the optional mid-flight hook — so a test can install the closure between the
    /// face's resolve and its catch, exactly the window the command lands in.</summary>
    private static Func<object> Fenced(Action? midFlight = null) => () =>
    {
        midFlight?.Invoke();
        return new RpcException(new Status(StatusCode.InvalidArgument,
            "the round is closed by the end operation end-op-retry"));
    };

    private static GatewayRoutingMonitor.RoomRouteTarget Target(
        string owner, long epoch, string endpoint) =>
        new("room-retry", "tenant-a", "game-a", "binding-a", owner, epoch, endpoint, 7);

    private sealed record FaceResponse(int Status, List<JsonElement> Receipts);

    /// <summary>Drives one simulator webhook through the face with the scripted doubles: the
    /// backoffs are zero so the retry posture itself is what the outcomes script.</summary>
    private static async Task<FaceResponse> PostAsync(ScriptedRoomEvents roomEvents,
        IRoomRoutingDirectory directory, ScriptedIngest ingest, string body,
        StopDeliveryRecord? stopDeliveries = null)
    {
        var endpoint = new PlatformIngressEndpoint(new AllowAllPlatformEventVerifier(),
            [new SimulatorPlatformEventMapper()], ingest, directory,
            _ => Grpc.Net.Client.GrpcChannel.ForAddress("http://127.0.0.1:1"),
            _ => roomEvents, [TimeSpan.Zero, TimeSpan.Zero], stopDeliveries);
        var context = new DefaultHttpContext();
        // The typed results the face returns serialize through the request's JSON options —
        // an empty provider with the defaults is all they need.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<IOptions<JsonOptions>>(
            Microsoft.Extensions.Options.Options.Create(new JsonOptions()));
        services.ConfigureHttpJsonOptions(_ => { });
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Headers[PlatformIngressEndpoint.AppIdHeader] = "app-1";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await (await endpoint.HandleAsync("simulator", context)).ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        var receipts = document.RootElement.TryGetProperty("receipts", out var array)
            ? array.EnumerateArray().Select(receipt => receipt.Clone()).ToList()
            : [];
        return new FaceResponse(context.Response.StatusCode, receipts);
    }

    /// <summary>The owning game host as a script: every push is recorded and answered by the
    /// next queued outcome, a reply or an RpcException the face must classify.</summary>
    private sealed class ScriptedRoomEvents(params object[] outcomes) : RoomEventsClient
    {
        private readonly Queue<object> _outcomes = new(outcomes);
        public readonly List<DeliverRoomEventRequest> Seen = new();

        public override AsyncUnaryCall<DeliverRoomEventReply> DeliverEventAsync(
            DeliverRoomEventRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            Seen.Add(request);
            var outcome = _outcomes.Dequeue();
            // A factory outcome runs at dequeue time — mid-push, where the mid-flight hook
            // of a fenced reply wants the closure installed.
            if (outcome is Func<object> factory) outcome = factory();
            if (outcome is RpcException error)
                throw error;
            var reply = (DeliverRoomEventReply)outcome;
            return new AsyncUnaryCall<DeliverRoomEventReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The durable attribution leg as a script: every batch is recorded and answered
    /// with one committed receipt carrying the event id as its dedup key.</summary>
    private sealed class ScriptedIngest : PlatformIngest.PlatformIngestClient
    {
        public int Calls;
        public string? LastDedupKey;

        public override AsyncUnaryCall<IngestBatchReply> IngestBatchAsync(
            IngestBatchRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDedupKey = request.Events.Count > 0 ? request.Events[0].SourceDedupKey : null;
            var reply = new IngestBatchReply();
            if (request.Events.Count > 0)
                reply.Receipts.Add(new EventReceipt
                {
                    SourceDedupKey = request.Events[0].SourceDedupKey,
                    Disposition = "Durable"
                });
            return new AsyncUnaryCall<IngestBatchReply>(Task.FromResult(reply),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The routing directory as a script: the stale target serves until the first
    /// refresh, the fresh one after — the picture the control plane's snapshot would give.</summary>
    private sealed class ScriptedDirectory(
        GatewayRoutingMonitor.RoomRouteTarget stale,
        GatewayRoutingMonitor.RoomRouteTarget? fresh) : IRoomRoutingDirectory
    {
        public int Refreshes { get; private set; }

        public bool TryResolve(string platform, string canonicalLiveKey,
            out GatewayRoutingMonitor.RoomRouteTarget target)
        {
            // A refresh with no fresh picture leaves the old one in place — the control plane
            // the monitor cannot reach changes nothing.
            target = (Refreshes > 0 ? fresh ?? stale : stale)!;
            return true;
        }

        public Task<bool> RefreshAsync(CancellationToken cancellationToken)
        {
            Refreshes++;
            return Task.FromResult(true);
        }
    }
}
