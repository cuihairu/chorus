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
/// off and retries. The scripted doubles hold the whole world between the face and the wire.
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

    private static DeliverRoomEventReply Reply(string disposition) => new() { Disposition = disposition };

    private static RpcException Unreachable() =>
        new(new Status(StatusCode.Unavailable, "the owner endpoint refused the connection"));

    private static GatewayRoutingMonitor.RoomRouteTarget Target(
        string owner, long epoch, string endpoint) =>
        new("room-retry", "tenant-a", "game-a", "binding-a", owner, epoch, endpoint);

    private sealed record FaceResponse(int Status, List<JsonElement> Receipts);

    /// <summary>Drives one simulator webhook through the face with the scripted doubles: the
    /// backoffs are zero so the retry posture itself is what the outcomes script.</summary>
    private static async Task<FaceResponse> PostAsync(ScriptedRoomEvents roomEvents,
        IRoomRoutingDirectory directory, ScriptedIngest ingest, string body)
    {
        var endpoint = new PlatformIngressEndpoint(new AllowAllPlatformEventVerifier(),
            [new SimulatorPlatformEventMapper()], ingest, directory,
            _ => Grpc.Net.Client.GrpcChannel.ForAddress("http://127.0.0.1:1"),
            _ => roomEvents, [TimeSpan.Zero, TimeSpan.Zero]);
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
