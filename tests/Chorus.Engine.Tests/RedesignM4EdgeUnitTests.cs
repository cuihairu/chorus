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
}
