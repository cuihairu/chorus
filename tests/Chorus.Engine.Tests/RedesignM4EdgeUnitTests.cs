using System.Text.Json;
using Chorus.Redesign.StreamerGateway;
using Chorus.Redesign.StreamerGateway.Messages;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The pure edge components of M4-1: the wire JSON shape, the tenant application map that
/// must refuse ambiguity at startup, the per-connection downlink outbox whose snapshot
/// re-baselines supersede chained deltas, and the fixed-window rate counter behind the
/// per-class limits.
/// </summary>
public sealed class RedesignM4EdgeUnitTests
{
    [Fact]
    public void EdgeJsonUsesCamelCaseContractFields()
    {
        var pong = JsonSerializer.Serialize(new EdgePong { ServerTimeMs = 1234 }, EdgeJson.Options);
        Assert.Contains("\"serverTimeMs\":1234", pong);
        var error = JsonSerializer.Serialize(new EdgeError
        {
            Code = "rate_limited", Message = "budget exhausted", OperationId = "op-1"
        }, EdgeJson.Options);
        Assert.Contains("\"code\":\"rate_limited\"", error);
        Assert.Contains("\"operationId\":\"op-1\"", error);
        // Byte fields ride as base64 on the wire (snapshot payloads, secrets).
        var bytes = JsonSerializer.Serialize(new { secret = new byte[] { 1, 2, 3 } }, EdgeJson.Options);
        Assert.Contains("\"secret\":\"AQID\"", bytes);
        var hello = JsonSerializer.Deserialize<EdgeHello>(
            """{"protocolVersion":1,"clientBuild":"b-1","supportedModuleVersions":["1.0.0"]}""",
            EdgeJson.Options);
        Assert.Equal(EdgeProtocolVersion.Current, hello!.ProtocolVersion);
        Assert.Equal("b-1", hello.ClientBuild);
        Assert.Equal(["1.0.0"], hello.SupportedModuleVersions);
    }

    [Fact]
    public void TenantApplicationMapResolvesAppsAndRefusesAmbiguity()
    {
        var map = new TenantApplicationMap("""{"tenant-a":{"app-1":"game-a"},"tenant-b":{"app-9":"game-b"}}""");
        Assert.True(map.TryResolve("app-1", out var tenant, out var game));
        Assert.Equal("tenant-a", tenant);
        Assert.Equal("game-a", game);
        Assert.False(map.TryResolve("app-missing", out _, out _));

        // One app id under two tenants is unresolvable at request time, so it is refused at
        // startup — the edge never routes one tenant's players into another's data.
        Assert.Throws<InvalidOperationException>(() =>
            new TenantApplicationMap("""{"tenant-a":{"app-1":"game-a"},"tenant-b":{"app-1":"game-b"}}"""));
        Assert.Throws<InvalidOperationException>(() => new TenantApplicationMap("{}"));
        Assert.Throws<InvalidOperationException>(() => new TenantApplicationMap("[]"));
    }

    [Fact]
    public async Task EdgeOutboxKeepsTheChainAndRebaselinesOnSnapshot()
    {
        using var outbox = new EdgeOutbox(capacity: 2);
        Assert.True(outbox.TryEnqueueOrdered("""{"type":"delta","n":1}"""));
        Assert.True(outbox.TryEnqueueOrdered("""{"type":"delta","n":2}"""));
        // The ordered queue is full: a chained message is refused wholesale and only a
        // snapshot can put the client back on the chain — never a gap in the middle.
        Assert.False(outbox.TryEnqueueOrdered("""{"type":"delta","n":3}"""));
        Assert.True(outbox.NeedsSnapshot);
        Assert.Equal(1, outbox.RefusedDeltas);
        // Sticky entries — the round result receipt — are never refused.
        Assert.True(outbox.TryEnqueueOrdered("""{"type":"roundResult"}""", sticky: true));

        // A snapshot supersedes the queued deltas and the outstanding re-baseline request.
        outbox.EnqueueSnapshot("""{"type":"snapshot","n":9}""");
        outbox.EnqueueSnapshot("""{"type":"snapshot","n":10}""");
        Assert.False(outbox.NeedsSnapshot);
        Assert.Equal(2, outbox.SupersededDeltas);
        Assert.Equal(1, outbox.SupersededSnapshots);

        var sent = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pump = Task.Run(() => outbox.RunAsync((frame, _) =>
        {
            sent.Add(frame);
            // The newest snapshot plus the sticky result are the two frames that survive.
            if (sent.Count == 2) done.TrySetResult();
            return Task.CompletedTask;
        }, cancellation.Token));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Order: the newest snapshot first, then the sticky result — superseded deltas never
        // reach the wire.
        await Task.Delay(200, CancellationToken.None);
        Assert.Equal("""{"type":"snapshot","n":10}""", sent[0]);
        Assert.Equal("""{"type":"roundResult"}""", sent[^1]);
        Assert.DoesNotContain(sent, frame => frame.Contains("delta", StringComparison.Ordinal));
        cancellation.Cancel();
        // The pump exits on cancellation — observed, not assumed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump);
    }

    [Fact]
    public void EdgeRateCounterRollsTheWindowAndCountsRejections()
    {
        var now = DateTimeOffset.UnixEpoch;
        var counter = new EdgeRateCounter(2, TimeSpan.FromSeconds(60), () => now);
        Assert.True(counter.TryConsume());
        Assert.True(counter.TryConsume());
        Assert.False(counter.TryConsume());
        Assert.Equal(1, counter.Rejected);
        // Inside the window the budget stays exhausted.
        now += TimeSpan.FromSeconds(30);
        Assert.False(counter.TryConsume());
        Assert.Equal(2, counter.Rejected);
        // The fixed window rolls: a fresh budget, no borrowed slack.
        now += TimeSpan.FromSeconds(31);
        Assert.True(counter.TryConsume());
        Assert.Equal(2, counter.Rejected);
    }

    [Fact]
    public void EdgeTypeNamesMatchTheClientProtocol()
    {
        // The names are the contract (redesign-contracts 3.1, 3.2): any drift here is a wire
        // break, so the constants are pinned to their exact spellings.
        Assert.Equal("hello", EdgeTypes.Hello);
        Assert.Equal("loginWithPlatformLaunch", EdgeTypes.LoginWithPlatformLaunch);
        Assert.Equal("resume", EdgeTypes.Resume);
        Assert.Equal("startGameplay", EdgeTypes.StartGameplay);
        Assert.Equal("joinQueue", EdgeTypes.JoinQueue);
        Assert.Equal("cancelQueue", EdgeTypes.CancelQueue);
        Assert.Equal("gameCommand", EdgeTypes.GameCommand);
        Assert.Equal("ack", EdgeTypes.Ack);
        Assert.Equal("requestSnapshot", EdgeTypes.RequestSnapshot);
        Assert.Equal("leaveRound", EdgeTypes.LeaveRound);
        Assert.Equal("logout", EdgeTypes.Logout);
        Assert.Equal("ping", EdgeTypes.Ping);
        Assert.Equal("loginReady", EdgeTypes.LoginReady);
        Assert.Equal("gameplayStatus", EdgeTypes.GameplayStatus);
        Assert.Equal("matchStatus", EdgeTypes.MatchStatus);
        Assert.Equal("roomJoined", EdgeTypes.RoomJoined);
        Assert.Equal("snapshot", EdgeTypes.Snapshot);
        Assert.Equal("delta", EdgeTypes.Delta);
        Assert.Equal("commandReceipt", EdgeTypes.CommandReceipt);
        Assert.Equal("sessionReplaced", EdgeTypes.SessionReplaced);
        Assert.Equal("roomSuspended", EdgeTypes.RoomSuspended);
        Assert.Equal("roundResult", EdgeTypes.RoundResult);
        Assert.Equal("resyncRequired", EdgeTypes.ResyncRequired);
        Assert.Equal("pong", EdgeTypes.Pong);
        Assert.Equal("error", EdgeTypes.Error);
        Assert.Equal(1, EdgeProtocolVersion.Current);
    }

    [Fact]
    public void SessionUplinkAndDownlinkJsonMatchTheContractFields()
    {
        // Downlink: the discriminator rides on every frame, camelCase fields, no extras.
        var ready = JsonSerializer.Serialize(new EdgeLoginReady
        {
            StreamerId = "streamer-1", SessionId = "session-1", SessionVersion = 2,
            AccessCredential = "access-1", AccessExpiresAt = 1760000000000, ResumeRegistered = true
        }, EdgeJson.Options);
        Assert.Contains("\"type\":\"loginReady\"", ready);
        Assert.Contains("\"streamerId\":\"streamer-1\"", ready);
        Assert.Contains("\"sessionVersion\":2", ready);
        Assert.Contains("\"accessExpiresAt\":1760000000000", ready);
        Assert.Contains("\"resumeRegistered\":true", ready);
        var replaced = JsonSerializer.Serialize(new EdgeSessionReplaced { Reason = "replaced_by_login" },
            EdgeJson.Options);
        Assert.Equal("""{"type":"sessionReplaced","reason":"replaced_by_login"}""", replaced);

        // Uplink: secrets ride as base64, missing optional fields stay absent.
        var login = JsonSerializer.Deserialize<EdgeLoginWithPlatformLaunch>(
            """{"platform":"simulator","appId":"app-1","launchCredential":"credential-1","loginAttemptId":"attempt-1","nextResumeSecret":"AQIDBA=="}""",
            EdgeJson.Options);
        Assert.Equal("app-1", login!.AppId);
        Assert.Equal([1, 2, 3, 4], login.NextResumeSecret);
        var resume = JsonSerializer.Deserialize<EdgeResume>(
            """{"resumeOperationId":"op-1","resumeCredential":"AQID","nextResumeSecret":"BAAA","previousSessionId":"session-1","lastSeenRoomId":"room-9","lastAppliedStateVersion":41}""",
            EdgeJson.Options);
        Assert.Equal("op-1", resume!.ResumeOperationId);
        Assert.Equal("room-9", resume.LastSeenRoomId);
        Assert.Equal(41, resume.LastAppliedStateVersion);
    }

    [Fact]
    public void EdgeConnectionGuardsSessionScoredUplinksUntilLoginEstablishesFacts()
    {
        var connection = new EdgeConnection("connection-1", "gateway-1", new EdgeOptions());
        Assert.False(connection.HasSession);
        Assert.True(EdgeTypes.RequiresSession(EdgeTypes.GameCommand));
        Assert.True(EdgeTypes.RequiresSession(EdgeTypes.Ack));
        Assert.True(EdgeTypes.RequiresSession(EdgeTypes.StartGameplay));
        Assert.False(EdgeTypes.RequiresSession(EdgeTypes.LoginWithPlatformLaunch));
        Assert.False(EdgeTypes.RequiresSession(EdgeTypes.Resume));
        Assert.False(EdgeTypes.RequiresSession(EdgeTypes.Logout));
        Assert.False(EdgeTypes.RequiresSession(EdgeTypes.Ping));

        connection.MarkSession("tenant-a", "session-1", "streamer-1", "access-1");
        Assert.True(connection.HasSession);
        Assert.Equal("tenant-a", connection.TenantId);
        Assert.Equal("session-1", connection.SessionId);
        Assert.Equal("access-1", connection.AccessCredential);
    }

    [Fact]
    public void RpcRefusalsMapToContractErrorCodes()
    {
        Assert.Equal(EdgeErrorCodes.SessionRevoked,
            EdgeErrorMapping.FromRpcStatus(Grpc.Core.StatusCode.PermissionDenied));
        Assert.Equal(EdgeErrorCodes.SessionReplaced,
            EdgeErrorMapping.FromRpcStatus(Grpc.Core.StatusCode.FailedPrecondition));
        Assert.Equal(EdgeErrorCodes.InvalidMessage,
            EdgeErrorMapping.FromRpcStatus(Grpc.Core.StatusCode.InvalidArgument));
        // Anything else is the gateway's own failure, never a borrowed session code.
        Assert.Equal(EdgeErrorCodes.InternalError,
            EdgeErrorMapping.FromRpcStatus(Grpc.Core.StatusCode.Unavailable));
    }

    [Fact]
    public void ErrorCodesArePinnedToTheirWireSpellings()
    {
        Assert.Equal("protocol_mismatch", EdgeErrorCodes.ProtocolMismatch);
        Assert.Equal("message_too_large", EdgeErrorCodes.MessageTooLarge);
        Assert.Equal("rate_limited", EdgeErrorCodes.RateLimited);
        Assert.Equal("no_session", EdgeErrorCodes.NoSession);
        Assert.Equal("invalid_message", EdgeErrorCodes.InvalidMessage);
        Assert.Equal("gateway_full", EdgeErrorCodes.GatewayFull);
        Assert.Equal("session_revoked", EdgeErrorCodes.SessionRevoked);
        Assert.Equal("session_replaced", EdgeErrorCodes.SessionReplaced);
        Assert.Equal("internal_error", EdgeErrorCodes.InternalError);
    }

    [Fact]
    public void GameplayAndMatchUplinkAndDownlinkJsonMatchTheContractFields()
    {
        // Uplinks: discriminators ride on every frame, optional facts stay absent.
        var start = JsonSerializer.Serialize(new EdgeStartGameplay
        {
            OperationId = "start-op-1", GameId = "game-a", RulesVersion = "rules-1"
        }, EdgeJson.Options);
        Assert.Contains("\"type\":\"startGameplay\"", start);
        Assert.Contains("\"operationId\":\"start-op-1\"", start);
        Assert.Contains("\"rulesVersion\":\"rules-1\"", start);
        var bare = JsonSerializer.Serialize(new EdgeStartGameplay
        {
            OperationId = "start-op-2", GameId = "game-a"
        }, EdgeJson.Options);
        Assert.DoesNotContain("rulesVersion", bare);
        var join = JsonSerializer.Deserialize<EdgeJoinQueue>(
            """{"type":"joinQueue","operationId":"join-op-1","queueType":"standard","options":"{\"region\":\"eu\"}"}""",
            EdgeJson.Options);
        Assert.Equal("standard", join!.QueueType);
        Assert.Equal("""{"region":"eu"}""", join.Options);
        var cancel = JsonSerializer.Deserialize<EdgeCancelQueue>(
            """{"type":"cancelQueue","operationId":"cancel-op-1","ticketId":"ticket-1","ticketVersion":3}""",
            EdgeJson.Options);
        Assert.Equal("ticket-1", cancel!.TicketId);
        Assert.Equal(3, cancel.TicketVersion);

        // Downlinks: gameplayStatus and matchStatus carry exactly the contract facts.
        var gameplay = JsonSerializer.Serialize(new EdgeGameplayStatus
        {
            BindingId = "binding-1", Generation = 2, State = "Ready"
        }, EdgeJson.Options);
        Assert.Contains("\"type\":\"gameplayStatus\"", gameplay);
        Assert.Contains("\"bindingId\":\"binding-1\"", gameplay);
        Assert.Contains("\"generation\":2", gameplay);
        Assert.Contains("\"state\":\"Ready\"", gameplay);
        var match = JsonSerializer.Serialize(new EdgeMatchStatus
        {
            TicketId = "ticket-1", State = "Matched", ReservationId = "res-1", RoomId = "room-1"
        }, EdgeJson.Options);
        Assert.Contains("\"type\":\"matchStatus\"", match);
        Assert.Contains("\"ticketId\":\"ticket-1\"", match);
        Assert.Contains("\"state\":\"Matched\"", match);
        Assert.Contains("\"reservationId\":\"res-1\"", match);
        Assert.Contains("\"roomId\":\"room-1\"", match);
        var queued = JsonSerializer.Serialize(new EdgeMatchStatus
        {
            TicketId = "ticket-2", State = "Queued"
        }, EdgeJson.Options);
        Assert.DoesNotContain("reservationId", queued);
        Assert.DoesNotContain("roomId", queued);
    }

    [Fact]
    public void QueuePartitionsAreComposedFromTheFactsTheGatewayCanName()
    {
        // Candidates meet only inside one partition: the four named facts decide it, and the
        // join options blob never widens the window (contracts 2, Match row).
        Assert.Equal("tenant-a/game-a/rules-1/standard",
            EdgeQueueKeys.PartitionFor("tenant-a", "game-a", "rules-1", "standard"));
        Assert.Equal("tenant-a/game-a/rules-1/standard",
            EdgeQueueKeys.PartitionFor("tenant-a", "game-a", "rules-1", "standard"));
        // A missing rules version is explicit, not silently shared with rules-1.
        Assert.Equal("tenant-a/game-a/-/standard",
            EdgeQueueKeys.PartitionFor("tenant-a", "game-a", null, "standard"));
        Assert.NotEqual(EdgeQueueKeys.PartitionFor("tenant-a", "game-a", "rules-1", "standard"),
            EdgeQueueKeys.PartitionFor("tenant-a", "game-a", "rules-2", "standard"));
        Assert.NotEqual(EdgeQueueKeys.PartitionFor("tenant-a", "game-a", "rules-1", "standard"),
            EdgeQueueKeys.PartitionFor("tenant-a", "game-a", "rules-1", "vip"));
    }

    [Fact]
    public void TicketStatesMapToTheClientVocabulary()
    {
        // The store's Committed is the protocol's Matched; every other state passes through
        // with its own name — the client never sees a store-internal word dressed up.
        Assert.Equal("Matched", EdgeMatchStates.FromStore("Committed"));
        Assert.Equal("Queued", EdgeMatchStates.FromStore("Queued"));
        Assert.Equal("Reserved", EdgeMatchStates.FromStore("Reserved"));
        Assert.Equal("Cancelled", EdgeMatchStates.FromStore("Cancelled"));
        Assert.Equal("Expired", EdgeMatchStates.FromStore("Expired"));
        // The queue's own refusal statuses never borrow the session codes.
        Assert.Equal(EdgeErrorCodes.InvalidMessage,
            EdgeErrorMapping.FromQueueRpcStatus(Grpc.Core.StatusCode.FailedPrecondition));
        Assert.Equal(EdgeErrorCodes.InvalidMessage,
            EdgeErrorMapping.FromQueueRpcStatus(Grpc.Core.StatusCode.InvalidArgument));
        Assert.Equal(EdgeErrorCodes.SessionRevoked,
            EdgeErrorMapping.FromQueueRpcStatus(Grpc.Core.StatusCode.PermissionDenied));
        // The session mapping is unchanged for the session round-trips.
        Assert.Equal(EdgeErrorCodes.SessionReplaced,
            EdgeErrorMapping.FromRpcStatus(Grpc.Core.StatusCode.FailedPrecondition));
    }

    [Fact]
    public async Task TheRoutingMonitorNotifiesEachRoomChangeOncePerSubscriber()
    {
        var monitor = new EdgeRoutingMonitor(() => throw new InvalidOperationException(
            "The unit test drives snapshots directly."));
        var notices = new List<(string Connection, string Room)>();
        monitor.Subscribe("streamer-1", "connection-1",
            room => { notices.Add(("connection-1", room)); return Task.CompletedTask; });
        monitor.Subscribe("streamer-2", "connection-2",
            room => { notices.Add(("connection-2", room)); return Task.CompletedTask; });

        // First apply: both subscribers learn the room as it is — including "no room yet".
        await monitor.ApplySnapshotAsync(new Chorus.Redesign.Rpc.RoutingSnapshotReply
        {
            Streamers =
            {
                new Chorus.Redesign.Rpc.StreamerRouteProto { StreamerId = "streamer-1", RoomId = "" },
                new Chorus.Redesign.Rpc.StreamerRouteProto { StreamerId = "streamer-2", RoomId = "room-9" }
            }
        });
        Assert.Equal([("connection-1", ""), ("connection-2", "room-9")], notices);

        // An unrelated change re-notifies only the streamer whose room moved; a repeat of the
        // same snapshot notifies nobody.
        notices.Clear();
        await monitor.ApplySnapshotAsync(new Chorus.Redesign.Rpc.RoutingSnapshotReply
        {
            Streamers =
            {
                new Chorus.Redesign.Rpc.StreamerRouteProto { StreamerId = "streamer-1", RoomId = "room-1" },
                new Chorus.Redesign.Rpc.StreamerRouteProto { StreamerId = "streamer-2", RoomId = "room-9" }
            }
        });
        Assert.Equal([("connection-1", "room-1")], notices);
        notices.Clear();
        await monitor.ApplySnapshotAsync(new Chorus.Redesign.Rpc.RoutingSnapshotReply
        {
            Streamers =
            {
                new Chorus.Redesign.Rpc.StreamerRouteProto { StreamerId = "streamer-1", RoomId = "room-1" },
                new Chorus.Redesign.Rpc.StreamerRouteProto { StreamerId = "streamer-2", RoomId = "room-9" }
            }
        });
        Assert.Empty(notices);
        Assert.Equal("room-1", monitor.RoomFor("streamer-1"));
        Assert.Null(monitor.RoomFor("streamer-none"));

        // Unsubscribed connections stop hearing about rooms.
        monitor.Unsubscribe("connection-1");
        notices.Clear();
        await monitor.ApplySnapshotAsync(new Chorus.Redesign.Rpc.RoutingSnapshotReply
        {
            Streamers =
            {
                new Chorus.Redesign.Rpc.StreamerRouteProto { StreamerId = "streamer-1", RoomId = "" }
            }
        });
        Assert.Empty(notices);
    }
}
