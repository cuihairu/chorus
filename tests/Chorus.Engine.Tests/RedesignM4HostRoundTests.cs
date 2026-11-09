using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Chorus.Platform;
using Chorus.Rpc;
using Chorus.StreamerGateway.Messages;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The M4-4 room chain over the full cluster (contracts 3.2): a match the coordinator pairs
/// becomes a live room on the game host, and each edge connection hears the whole committed
/// record — roomJoined naming its side, the head snapshot, deltas whose base versions chain,
/// and the frozen result that ends the round. The recovery legs follow the same rule: a
/// dropped connection rejoins by resume with a fresh snapshot at its declared baseline, a
/// requestSnapshot re-baselines the chain, acknowledgements are recorded and never answered,
/// and a room that parks itself surfaces as roomSuspended — visibly, never as a freeze.
/// M4-5 adds the command legs on the same cluster: a game command is Accepted (durably in
/// the record) then Applied with the delta that consumed it, a refused command is a visible
/// error on a connection that stays open, and a LeaveRound withdraws the mount quietly while
/// the room settles for everyone still in it.
/// </summary>
public sealed class RedesignM4HostRoundTests
{
    [Fact]
    public async Task MatchedClientsReceiveTheFullRoomChainToAResult()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        var mountA = await LoginAndStartAsync(first, simulator, "open-m4-room-a");
        var mountB = await LoginAndStartAsync(second, simulator, "open-m4-room-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var roomA = await AwaitMatchAsync(first);
        var roomB = await AwaitMatchAsync(second);
        Assert.Equal(roomA, roomB);

        // The full chain, on both connections: roomJoined → snapshot → chained deltas → the
        // frozen result (AT06/AT15's client face).
        var chainA = await ReadUntilAsync(first, EdgeTypes.RoundResult, TimeSpan.FromSeconds(30));
        var chainB = await ReadUntilAsync(second, EdgeTypes.RoundResult, TimeSpan.FromSeconds(30));
        AssertChain(chainA, roomA, mountA);
        AssertChain(chainB, roomA, mountB);
        // The same round result reaches both sides: one result id, composed from the
        // immutable room/epoch/tick triple.
        Assert.Equal(ResultIdOf(chainA), ResultIdOf(chainB));
    }

    [Fact]
    public async Task ADroppedClientRejoinsItsRoundByResumeWithAFreshSnapshot()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        var dropped = await LoginAndStartAsync(first, simulator, "open-m4-drop");
        await LoginAndStartAsync(second, simulator, "open-m4-drop-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);

        // Read until the first delta, then drop the connection mid-round: the round runs on.
        var head = await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        var appliedVersion = head[^1].GetProperty("stateVersion").GetInt64();
        first.Dispose();

        // The resume carries the session, the room it last saw, and the version it applied.
        // The server's routing record decides the room; the answer is a snapshot at or above
        // the declared baseline (contracts 3.3: resume never re-redeems the platform token).
        var resumeSecret = RandomNumberGenerator.GetBytes(32);
        using var resumed = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await resumed.HelloAsync();
        await resumed.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-m4-drop",
            ResumeCredential = dropped.Secret,
            NextResumeSecret = resumeSecret,
            PreviousSessionId = dropped.SessionId,
            LastSeenRoomId = room,
            LastAppliedStateVersion = appliedVersion
        });
        await resumed.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        var rejoined = await resumed.ExpectAsync(EdgeTypes.RoomJoined, TimeSpan.FromSeconds(30));
        Assert.Equal(room, rejoined.GetProperty("roomId").GetString());
        Assert.False(string.IsNullOrEmpty(rejoined.GetProperty("participation").GetString()));
        var snapshot = await resumed.ExpectAsync(EdgeTypes.Snapshot, TimeSpan.FromSeconds(30));
        Assert.Equal(room, snapshot.GetProperty("roomId").GetString());
        Assert.True(snapshot.GetProperty("stateVersion").GetInt64() >= appliedVersion);
        // The rejoined chain runs to the same frozen result the round was always going to.
        var tail = await ReadUntilAsync(resumed, EdgeTypes.RoundResult, TimeSpan.FromSeconds(30));
        Assert.Equal(ResultIdOf(tail), ResultIdOf(await DrainResultAsync(second)));
    }

    [Fact]
    public async Task ResumeMidRoundReentersTheFencedRoomAndAResumeWithNoRoomReentersNothing()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        var dropped = await LoginAndStartAsync(first, simulator, "open-m4-fence-resume");
        await LoginAndStartAsync(second, simulator, "open-m4-fence-resume-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);
        var head = await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        var appliedVersion = head[^1].GetProperty("stateVersion").GetInt64();
        first.Dispose();

        // Part 1: a mid-round resume re-enters its room through the fenced route (the same
        // server participation record that named the push names the re-entry).
        var resumeSecret = RandomNumberGenerator.GetBytes(32);
        using var resumed = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await resumed.HelloAsync();
        await resumed.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-fence-drop",
            ResumeCredential = dropped.Secret,
            NextResumeSecret = resumeSecret,
            PreviousSessionId = dropped.SessionId,
            LastSeenRoomId = room,
            LastAppliedStateVersion = appliedVersion
        });
        var resumedReady = await resumed.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        Assert.False(string.IsNullOrEmpty(resumedReady.GetProperty("accessCredential").GetString()));
        var rejoined = await resumed.ExpectAsync(EdgeTypes.RoomJoined, TimeSpan.FromSeconds(30));
        Assert.Equal(room, rejoined.GetProperty("roomId").GetString());

        // Part 2: a mount that holds no round resumes into nothing. The server's participation
        // record decides — even a client-claimed lastSeenRoomId does not put the mount in a
        // room the gateway then joins (room-recovery.md:39, architecture.md:279).
        using var standalone = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await standalone.HelloAsync();
        var freshMount = await LoginAndStartAsync(standalone, simulator, "open-m4-fence-noroom");
        standalone.Dispose();
        var freshSecret = RandomNumberGenerator.GetBytes(32);
        using var fresh = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await fresh.HelloAsync();
        await fresh.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-noroom",
            ResumeCredential = freshMount.Secret,
            NextResumeSecret = freshSecret,
            PreviousSessionId = freshMount.SessionId,
            LastSeenRoomId = room,
            LastAppliedStateVersion = 0
        });
        await fresh.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        // Nothing follows the ready frame: the strongest form of "joined nothing" is the
        // absence of a roomJoined within the window.
        string? unexpectedType = null;
        try
        {
            unexpectedType = (await fresh.ReceiveAsync(TimeSpan.FromSeconds(5)))?
                .GetProperty("type").GetString();
        }
        catch (TimeoutException)
        {
            // No frame at all: the mount was not seated into any room.
        }
        Assert.NotEqual(EdgeTypes.RoomJoined, unexpectedType);
    }

    /// <summary>Waits for the round release to clear the mount's participation room — the
    /// release transaction is the only writer that clears it, and the eligibility opens when
    /// its receipt commits (FIX-END-13).</summary>
    private static async Task WaitForRoomReleaseAsync(RedesignM0HostTests.HostEnvironment environment,
        string accessCredential)
    {
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "release-probe");
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await session.GameplayStatusAsync(new GameplayStatusRequest
            {
                AccessCredential = accessCredential, TenantId = "tenant-a"
            });
            if (status.CurrentRoomId is not { Length: > 0 }) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("The room release never cleared the mount's participation.");
    }

    [Fact]
    public async Task RequestSnapshotRebaselinesTheChainAndRefusesUnknownRooms()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        await LoginAndStartAsync(first, simulator, "open-m4-snap-a");
        await LoginAndStartAsync(second, simulator, "open-m4-snap-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);

        var head = await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        var roomEpoch = FirstFrameOf(head, EdgeTypes.RoomJoined).GetProperty("roomEpoch").GetInt64();
        var applied = head[^1].GetProperty("stateVersion").GetInt64();
        var appliedTick = head[^1].GetProperty("tickId").GetInt64();
        // A room the connection does not sit in is refused, not guessed at.
        await first.SendAsync(new EdgeRequestSnapshot { RoomId = "room-not-mine", RoomEpoch = roomEpoch });
        var refused = await first.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.InvalidMessage, refused.GetProperty("code").GetString());

        // The acknowledged position is recorded; the requested snapshot re-baselines the
        // chain at the authoritative head (contracts 3.1 Ack, RequestSnapshot).
        await first.SendAsync(new EdgeAck
        {
            RoomId = room, RoomEpoch = roomEpoch,
            LastAppliedTickId = appliedTick, StateVersion = applied
        });
        await first.SendAsync(new EdgeRequestSnapshot { RoomId = room, RoomEpoch = roomEpoch });
        var snapshot = await first.ExpectAsync(EdgeTypes.Snapshot, TimeSpan.FromSeconds(30));
        Assert.Equal(room, snapshot.GetProperty("roomId").GetString());
        Assert.True(snapshot.GetProperty("stateVersion").GetInt64() >= applied);
        // After the re-baseline the deltas keep chaining and the round still ends frozen.
        var tail = await ReadUntilAsync(first, EdgeTypes.RoundResult, TimeSpan.FromSeconds(30));
        Assert.True(tail[^1].GetProperty("resultId").GetString()!.Length > 0);
    }

    [Fact]
    public async Task AcknowledgementsAreSilentAndTheChainStaysUnbroken()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        await LoginAndStartAsync(first, simulator, "open-m4-ack-a");
        await LoginAndStartAsync(second, simulator, "open-m4-ack-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);
        var head = await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        var roomEpoch = FirstFrameOf(head, EdgeTypes.RoomJoined).GetProperty("roomEpoch").GetInt64();

        // A flood of acknowledgements past the per-minute budget is dropped silently
        // (contracts 3.1): no error, no echo — the chain frames are all that arrive.
        for (var i = 0; i < 650; i++)
            await first.SendAsync(new EdgeAck
            {
                RoomId = room, RoomEpoch = roomEpoch,
                LastAppliedTickId = 1, StateVersion = 1
            });
        var tail = await ReadUntilAsync(first, EdgeTypes.RoundResult, TimeSpan.FromSeconds(30));
        var whole = head.Concat(tail).ToList();
        Assert.DoesNotContain(whole, frame => frame.GetProperty("type").GetString() == EdgeTypes.Error);
        AssertChain(whole, room, null);
        // The connection survived the flood and still answers a ping.
        await first.SendRawAsync("""{"type":"ping"}""");
        await first.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AParkedRoomSurfacesAsRoomSuspendedOnTheClient()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await client.HelloAsync();
        var mount = await LoginAndStartAsync(client, simulator, "open-m4-parked");

        // A hand-started round whose module fails deterministically (the documented fault
        // seam): three strikes park the room visibly instead of dying.
        var room = "room-m4-parked";
        var epoch = await StartFailingRoundAsync(environment, room, mount.BindingId,
            mount.StreamerId);
        client.Dispose();

        // The client comes back by resume; the rejoin subscribes the parked room, and the
        // honest answer is roomSuspended — with the recovery attempt named — never a freeze.
        using var resumed = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await resumed.HelloAsync();
        await resumed.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-m4-parked",
            ResumeCredential = mount.Secret,
            NextResumeSecret = RandomNumberGenerator.GetBytes(32),
            PreviousSessionId = mount.SessionId,
            LastSeenRoomId = room
        });
        await resumed.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        // 存活性等待（事件不变量），不是性能断言：被停机的房间要先过 15s 所有权门
        // 与 5s 恢复扫掠的默认节拍，再经路由传播到边缘；CI 满载 runner 上各段都会
        // 拖长（本地典型 ~20s，CI 150s 窗口出现过超时），窗口按外部负载余量放宽。
        var suspended = await resumed.ExpectAsync(EdgeTypes.RoomSuspended, TimeSpan.FromSeconds(300));
        Assert.Equal(epoch, suspended.GetProperty("recoveryAttempt").GetInt64());
    }

    [Fact]
    public async Task CommandsAreAcceptedThenAppliedWithTheirDelta()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        // A long round: the command must land while the room still ticks, not race its end.
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment, roundDurationTicks: 400);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        await LoginAndStartAsync(first, simulator, "open-m4-cmd-a");
        await LoginAndStartAsync(second, simulator, "open-m4-cmd-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);

        var head = await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        var roomEpoch = FirstFrameOf(head, EdgeTypes.RoomJoined).GetProperty("roomEpoch").GetInt64();
        const string commandId = "cmd-edge-1";
        await first.SendAsync(new EdgeGameCommand
        {
            CommandId = commandId, RoomId = room, RoomEpoch = roomEpoch, ClientSequence = 1,
            Payload = JsonDocument.Parse("""{"action":"boost","level":2}""").RootElement
        });
        // Accepted: durably in the round's input record — a receipt, not a game effect.
        var toAccepted = await ReadUntilAsync(first, EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30));
        var accepted = toAccepted[^1];
        Assert.Equal(commandId, accepted.GetProperty("commandId").GetString());
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());
        var receiptId = accepted.GetProperty("receiptId").GetString()!;
        Assert.True(receiptId.StartsWith(room + ":", StringComparison.Ordinal), receiptId);

        // Applied: rides right after the delta that consumed the command (contracts 3.2).
        var toApplied = await ReadUntilAsync(first, EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30));
        var applied = toApplied[^1];
        Assert.Equal(commandId, applied.GetProperty("commandId").GetString());
        Assert.Equal("Applied", applied.GetProperty("status").GetString());
        Assert.Contains(toApplied.Take(toApplied.Count - 1),
            frame => frame.GetProperty("type").GetString() == EdgeTypes.Delta);

        // An exact retry replays the same receipt: one command, one sequence, one id.
        await first.SendAsync(new EdgeGameCommand
        {
            CommandId = commandId, RoomId = room, RoomEpoch = roomEpoch, ClientSequence = 1,
            Payload = JsonDocument.Parse("""{"action":"boost","level":2}""").RootElement
        });
        var retried = (await ReadUntilAsync(first, EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30)))[^1];
        Assert.Equal("Accepted", retried.GetProperty("status").GetString());
        Assert.Equal(receiptId, retried.GetProperty("receiptId").GetString());

        // The connection stays a connection after all of it.
        await AssertPingWorksAsync(first);
    }

    [Fact]
    public async Task ACommandTheRoundCannotAcceptIsRefusedVisibly()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment, roundDurationTicks: 400);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        await LoginAndStartAsync(first, simulator, "open-m4-cmdx-a");
        await LoginAndStartAsync(second, simulator, "open-m4-cmdx-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);

        var head = await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        var roomEpoch = FirstFrameOf(head, EdgeTypes.RoomJoined).GetProperty("roomEpoch").GetInt64();
        // A stale epoch is fenced by the store; the edge says invalid_message and the
        // connection stays open for the command that names the live epoch.
        await first.SendAsync(new EdgeGameCommand
        {
            CommandId = "cmd-stale", RoomId = room, RoomEpoch = roomEpoch + 9, ClientSequence = 1,
            Payload = JsonDocument.Parse("""{"action":"boost"}""").RootElement
        });
        var refused = await first.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.InvalidMessage, refused.GetProperty("code").GetString());
        await first.SendAsync(new EdgeGameCommand
        {
            CommandId = "cmd-live", RoomId = room, RoomEpoch = roomEpoch, ClientSequence = 2,
            Payload = JsonDocument.Parse("""{"action":"boost"}""").RootElement
        });
        var accepted = await first.ExpectAsync(EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30));
        Assert.Equal("cmd-live", accepted.GetProperty("commandId").GetString());
        await AssertPingWorksAsync(first);
    }

    [Fact]
    public async Task LeaveRoundStopsFramesForTheLeaverWhileTheRoomSettles()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        await LoginAndStartAsync(first, simulator, "open-m4-leave-a");
        await LoginAndStartAsync(second, simulator, "open-m4-leave-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);
        await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        await ReadUntilAsync(second, EdgeTypes.Delta, TimeSpan.FromSeconds(30));

        // The leave is silent on the wire (contracts 3.1 has no leave acknowledgment) —
        // and a second leave with a new operation id is equally quiet.
        await first.SendAsync(new EdgeLeaveRound
        {
            OperationId = "leave-1", RoomId = room, Reason = "done"
        });
        await first.SendAsync(new EdgeLeaveRound
        {
            OperationId = "leave-2", RoomId = room, Reason = "retry"
        });

        // The room settles for everyone still in it: the stayer hears the frozen result.
        var settled = await ReadUntilAsync(second, EdgeTypes.RoundResult, TimeSpan.FromSeconds(30));
        Assert.Equal(room, ResultIdOf(settled).Split(':')[0]);
        // The leaver's ledger for the whole round, read in one pass: the room is over — the
        // result was its last possible frame — so everything the leaver ever received after
        // the leave is already in flight or already here. A timed-out read would abort the
        // client socket, so the pong of a ping sent now is the ledger's end mark: the frames
        // before it are everything that raced the leave (at most the one in-flight delta),
        // and among them the sticky result and any error must both be absent — the result
        // reaches every seat, so its absence is the seat's absence. The pong itself is the
        // other half of the assertion: a leaver is still a client, and the connection
        // answers.
        await first.SendRawAsync("""{"type":"ping"}""");
        var ledger = await ReadUntilAsync(first, EdgeTypes.Pong, TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(ledger, frame =>
            frame.GetProperty("type").GetString() is EdgeTypes.RoundResult or EdgeTypes.Error);
    }

    [Fact]
    public async Task LeaveRoundOnARoomTheMountIsNotInIsRefused()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await client.HelloAsync();
        await LoginAndStartAsync(client, simulator, "open-m4-leave-none");

        await client.SendAsync(new EdgeLeaveRound
        {
            OperationId = "leave-none", RoomId = "room-not-playing", Reason = "test"
        });
        var refused = await client.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.InvalidMessage, refused.GetProperty("code").GetString());
        await AssertPingWorksAsync(client);
    }

    [Fact]
    public async Task GameCommandBeforeGameplayIsRefusedVisibly()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var client = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await client.HelloAsync();
        // A login without a mount: the command has no round to name.
        var secret = RandomNumberGenerator.GetBytes(32);
        var credential = simulator.IssueCredential("app-1", "open-m4-cmd-early", "live-cmd-early",
            TimeSpan.FromMinutes(5));
        await client.SendAsync(new EdgeLoginWithPlatformLaunch
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-cmd-early", NextResumeSecret = secret
        });
        await client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        await client.SendAsync(new EdgeGameCommand
        {
            CommandId = "cmd-early", RoomId = "room-anywhere", RoomEpoch = 1, ClientSequence = 1,
            Payload = JsonDocument.Parse("""{"action":"boost"}""").RootElement
        });
        var refused = await client.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.InvalidMessage, refused.GetProperty("code").GetString());
        await AssertPingWorksAsync(client);
    }

    [Fact]
    public async Task AClientCommandReachesItsOwnRoomModuleAndComesBackApplied()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        // A long round: the command must land while the room still ticks, not race its end.
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment, roundDurationTicks: 400);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        await LoginAndStartAsync(first, simulator, "open-m4-link09-a");
        await LoginAndStartAsync(second, simulator, "open-m4-link09-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);

        var head = await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        var roomEpoch = FirstFrameOf(head, EdgeTypes.RoomJoined).GetProperty("roomEpoch").GetInt64();
        const string commandId = "cmd-link09-1";
        // FIX-LINK-09: the command rides the gateway's direct push to the owning game host and
        // the host's own durable client-command leg, then the host seats it for its next tick.
        // The proof of "reaches the module" is the boost's score effect in the committed delta
        // the Applied word follows — not merely that the command was accepted.
        await first.SendAsync(new EdgeGameCommand
        {
            CommandId = commandId, RoomId = room, RoomEpoch = roomEpoch, ClientSequence = 1,
            Payload = JsonDocument.Parse("""{"action":"boost","level":3}""").RootElement
        });
        var toAccepted = await ReadUntilAsync(first, EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30));
        var accepted = toAccepted[^1];
        Assert.Equal(commandId, accepted.GetProperty("commandId").GetString());
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());
        var acceptedReceiptId = accepted.GetProperty("receiptId").GetString()!;
        Assert.True(acceptedReceiptId.StartsWith(room + ":", StringComparison.Ordinal), acceptedReceiptId);

        // The delta that consumes the command carries its score effect — the module saw it.
        var toApplied = await ReadUntilAsync(first, EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30));
        var applied = toApplied[^1];
        Assert.Equal(commandId, applied.GetProperty("commandId").GetString());
        Assert.Equal("Applied", applied.GetProperty("status").GetString());
        // The Applied word rides right after the consuming delta, and the durable sequence the
        // Accepted receipt named is the same one the Applied word names — one command, one
        // sequence through the whole leg.
        var consuming = toApplied.Take(toApplied.Count - 1).Last(frame =>
            frame.GetProperty("type").GetString() == EdgeTypes.Delta
            && HasScoreEffect(frame.GetProperty("payload"), "red"));
        Assert.Equal(applied.GetProperty("receiptId").GetString(), acceptedReceiptId);
        Assert.Equal(room, consuming.GetProperty("roomId").GetString());

        // The connection stays a connection after the command consumed.
        await AssertPingWorksAsync(first);
    }

    [Fact]
    public async Task ACommandForARoomTheMountIsNotInIsRefused()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment, roundDurationTicks: 400);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        await LoginAndStartAsync(first, simulator, "open-m4-link09b-a");
        await LoginAndStartAsync(second, simulator, "open-m4-link09b-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);

        var head = await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        var roomEpoch = FirstFrameOf(head, EdgeTypes.RoomJoined).GetProperty("roomEpoch").GetInt64();
        // FIX-LINK-09 upstream fence: a command names the mount's own participation — a guessed
        // placement is refused at the gateway, never forwarded to a room the mount does not
        // play. The connection stays open and its own room still accepts the live command.
        await first.SendAsync(new EdgeGameCommand
        {
            CommandId = "cmd-link09-other", RoomId = "room-not-mine", RoomEpoch = roomEpoch,
            ClientSequence = 1, Payload = JsonDocument.Parse("""{"action":"boost"}""").RootElement
        });
        var refused = await first.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.InvalidMessage, refused.GetProperty("code").GetString());
        await first.SendAsync(new EdgeGameCommand
        {
            CommandId = "cmd-link09-own", RoomId = room, RoomEpoch = roomEpoch, ClientSequence = 2,
            Payload = JsonDocument.Parse("""{"action":"boost"}""").RootElement
        });
        var accepted = await first.ExpectAsync(EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30));
        Assert.Equal("cmd-link09-own", accepted.GetProperty("commandId").GetString());
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());
        await AssertPingWorksAsync(first);
    }

    [Fact]
    public async Task ALeavingSeatKeepsTheSharedRoomStreamAlive()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await StartEdgeClusterAsync(environment, roundDurationTicks: 400);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;
        using var first = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        using var second = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await first.HelloAsync();
        await second.HelloAsync();
        await LoginAndStartAsync(first, simulator, "open-m4-link10-a");
        await LoginAndStartAsync(second, simulator, "open-m4-link10-b");
        await QueueAsync(first, "join-a");
        await QueueAsync(second, "join-b");
        var room = await AwaitMatchAsync(first);
        await AwaitMatchAsync(second);

        // Both seats hear the room on the gateway's one shared subscription.
        await ReadUntilAsync(first, EdgeTypes.Delta, TimeSpan.FromSeconds(30));
        await ReadUntilAsync(second, EdgeTypes.Delta, TimeSpan.FromSeconds(30));

        // FIX-LINK-10: the first seat leaves. The shared subscription must survive the
        // departure — the second seat's chain continues without a re-baseline (no second
        // roomJoined for the same room), and the still-serving stream keeps carrying the
        // receipts of a command the remaining seat sends after the departure.
        first.Dispose();
        var continued = await ReadDeltasAsync(second, 3, TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(continued, frame =>
            frame.GetProperty("type").GetString() == EdgeTypes.RoomJoined);
        var roomEpoch = FirstFrameOf(continued, EdgeTypes.Delta).GetProperty("roomEpoch").GetInt64();
        await second.SendAsync(new EdgeGameCommand
        {
            CommandId = "cmd-link10-1", RoomId = room, RoomEpoch = roomEpoch,
            ClientSequence = 1,
            Payload = JsonDocument.Parse("""{"action":"boost","level":3}""").RootElement
        });
        // Accepted: durably in the round's input record. Applied: rides after the consuming
        // delta — proof the still-shared stream carries both lanes to the remaining seat.
        var toAccepted = await ReadUntilAsync(second, EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30));
        var accepted = toAccepted[^1];
        Assert.Equal("cmd-link10-1", accepted.GetProperty("commandId").GetString());
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());
        var toApplied = await ReadUntilAsync(second, EdgeTypes.CommandReceipt, TimeSpan.FromSeconds(30));
        var applied = toApplied[^1];
        Assert.Equal("cmd-link10-1", applied.GetProperty("commandId").GetString());
        Assert.Equal("Applied", applied.GetProperty("status").GetString());
        Assert.Contains(toApplied.Take(toApplied.Count - 1),
            frame => frame.GetProperty("type").GetString() == EdgeTypes.Delta);
        await AssertPingWorksAsync(second);
    }

    /// <summary>The module's score effect is the proof the command reached the module: the
    /// delta payload is the effect array, and the boost's score entry names the credited
    /// faction (the module's shape is {faction, delta, total}, not a per-faction field).</summary>
    private static bool HasScoreEffect(JsonElement payload, string faction)
    {
        if (payload.ValueKind != JsonValueKind.Array) return false;
        foreach (var effect in payload.EnumerateArray())
            if (effect.GetProperty("kind").GetString() == "score"
                && effect.GetProperty("payload").TryGetProperty("faction", out var credited)
                && credited.GetString() == faction)
                return true;
        return false;
    }

    // ---- helpers ----

    /// <summary>The liveness proof after any refusal: the connection is still a connection.</summary>
    private static async Task AssertPingWorksAsync(RedesignM4WsClient client)
    {
        // The ping uplink carries no fields beyond its type, so it goes out as raw text.
        await client.SendRawAsync("""{"type":"ping"}""");
        await client.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
    }

    private sealed record Mount(string SessionId, string StreamerId, string BindingId, byte[] Secret);

    private static async Task<Mount> LoginAndStartAsync(RedesignM4WsClient client,
        DevelopmentPlatformSimulator simulator, string openId)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId,
            TimeSpan.FromMinutes(5));
        await client.SendAsync(new EdgeLoginWithPlatformLaunch
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId, NextResumeSecret = secret
        });
        var ready = await client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        await client.SendAsync(new EdgeStartGameplay
        {
            OperationId = "start-" + openId, GameId = "game-a", RulesVersion = "rules-1"
        });
        var status = await client.ExpectAsync(EdgeTypes.GameplayStatus, TimeSpan.FromSeconds(30));
        return new Mount(ready.GetProperty("sessionId").GetString()!,
            ready.GetProperty("streamerId").GetString()!,
            status.GetProperty("bindingId").GetString()!, secret);
    }

    private static async Task QueueAsync(RedesignM4WsClient client, string operationId)
    {
        await client.SendAsync(new EdgeJoinQueue { OperationId = operationId, QueueType = "standard" });
        await client.ExpectAsync(EdgeTypes.MatchStatus, TimeSpan.FromSeconds(30));
    }

    private static async Task<string> AwaitMatchAsync(RedesignM4WsClient client)
    {
        var matched = await client.ExpectAsync(EdgeTypes.MatchStatus, TimeSpan.FromSeconds(30));
        Assert.Equal("Matched", matched.GetProperty("state").GetString());
        return matched.GetProperty("roomId").GetString()!;
    }

    /// <summary>Reads frames until one of the wanted type arrives, returning everything seen
    /// on the way — the chain assertions need the in-between frames, not just the answer.</summary>
    private static async Task<List<JsonElement>> ReadUntilAsync(RedesignM4WsClient client,
        string type, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var frames = new List<JsonElement>();
        while (DateTime.UtcNow < deadline)
        {
            JsonElement? frame;
            try
            {
                frame = await client.ReceiveAsync(deadline - DateTime.UtcNow);
            }
            catch (TimeoutException)
            {
                break;
            }
            if (frame is null) break;
            frames.Add(frame.Value);
            if (frame.Value.GetProperty("type").GetString() == type) return frames;
        }
        throw new TimeoutException($"Frame '{type}' never arrived (saw: "
            + string.Join(", ", frames.Select(f => f.GetProperty("type").GetString())) + ").");
    }

    /// <summary>The chain contract a client can hold the gateway to (AT06/AT15): the join
    /// precedes every frame, the snapshot precedes the deltas, every delta chains on the
    /// version before it, and the result freezes the same chain.</summary>
    private static void AssertChain(List<JsonElement> frames, string room, Mount? mount)
    {
        Assert.False(frames.Count == 0);
        var joined = FirstFrameOf(frames, EdgeTypes.RoomJoined);
        Assert.Equal(room, joined.GetProperty("roomId").GetString());
        Assert.False(string.IsNullOrEmpty(joined.GetProperty("moduleVersion").GetString()));
        Assert.False(string.IsNullOrEmpty(joined.GetProperty("configHash").GetString()));
        if (mount is not null)
            Assert.False(string.IsNullOrEmpty(joined.GetProperty("participation").GetString()));

        var snapshot = FirstFrameOf(frames, EdgeTypes.Snapshot);
        Assert.Equal(room, snapshot.GetProperty("roomId").GetString());
        Assert.True(snapshot.GetProperty("stateVersion").GetInt64() >= 1);
        Assert.False(string.IsNullOrEmpty(snapshot.GetProperty("payload").GetString()));

        long chained = snapshot.GetProperty("stateVersion").GetInt64();
        var deltas = 0;
        foreach (var frame in frames)
        {
            var frameType = frame.GetProperty("type").GetString();
            if (frameType == EdgeTypes.Snapshot)
            {
                // A snapshot re-baselines the client like any real one (a late joiner seats
                // itself from the head): the chain restarts at the version the snapshot
                // names, and the deltas after it chain on from there.
                chained = frame.GetProperty("stateVersion").GetInt64();
                continue;
            }
            if (frameType != EdgeTypes.Delta) continue;
            deltas++;
            Assert.Equal(room, frame.GetProperty("roomId").GetString());
            // No gaps, no replays: each delta applies to the version the chain last named.
            Assert.Equal(chained, frame.GetProperty("baseVersion").GetInt64());
            chained = frame.GetProperty("stateVersion").GetInt64();
            // The payload is the module's effect array — possibly empty, always an array.
            Assert.Equal(JsonValueKind.Array, frame.GetProperty("payload").ValueKind);
        }
        Assert.True(deltas >= 1);

        var result = frames[^1];
        Assert.Equal(EdgeTypes.RoundResult, result.GetProperty("type").GetString());
        Assert.Equal(room + ":", result.GetProperty("resultId").GetString()![..(room.Length + 1)]);
        Assert.Equal("Committed", result.GetProperty("settlementState").GetString());
        Assert.True(result.GetProperty("result").GetProperty("ranking").GetArrayLength() >= 1);
    }

    private static JsonElement FirstFrameOf(List<JsonElement> frames, string type) =>
        frames.First(frame => frame.GetProperty("type").GetString() == type);

    private static string ResultIdOf(List<JsonElement> frames) =>
        frames.First(frame => frame.GetProperty("type").GetString() == EdgeTypes.RoundResult)
            .GetProperty("resultId").GetString()!;

    /// <summary>Drains the remaining chain on a client that already saw its match, returning
    /// the frames up to and including the result.</summary>
    private static async Task<List<JsonElement>> DrainResultAsync(RedesignM4WsClient client) =>
        await ReadUntilAsync(client, EdgeTypes.RoundResult, TimeSpan.FromSeconds(30));

    /// <summary>Reads frames until the given number of deltas has arrived, returning every
    /// frame seen on the way — the shared-stream probe needs the in-between frames too.</summary>
    private static async Task<List<JsonElement>> ReadDeltasAsync(RedesignM4WsClient client,
        int deltaCount, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var frames = new List<JsonElement>();
        while (DateTime.UtcNow < deadline)
        {
            JsonElement? frame;
            try
            {
                frame = await client.ReceiveAsync(deadline - DateTime.UtcNow);
            }
            catch (TimeoutException)
            {
                break;
            }
            if (frame is null) break;
            frames.Add(frame.Value);
            var deltas = frames.Count(f => f.GetProperty("type").GetString() == EdgeTypes.Delta);
            if (deltas >= deltaCount) return frames;
        }
        throw new TimeoutException($"Only saw "
            + frames.Count(f => f.GetProperty("type").GetString() == EdgeTypes.Delta)
            + $" deltas of {deltaCount} (saw: "
            + string.Join(", ", frames.Select(f => f.GetProperty("type").GetString())) + ").");
    }

    /// <summary>Hand-starts a round whose module fails deterministically at tick 2 (the
    /// documented fault seam) and parks the room after its strikes. Returns the epoch.</summary>
    private static async Task<long> StartFailingRoundAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId, string bindingId,
        string streamerId)
    {
        using var controlCertificate = environment.Certificate("@cluster", "control-server",
            "control-m4-parked");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        var owner = await new ControlService.ControlServiceClient(controlChannel).ClaimOwnershipAsync(
            new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
                OwnerInstanceId = "game-1", LeaseSeconds = 30
            });
        using var gameCertificate = environment.Certificate("@cluster", "control-server",
            "rounds-m4-parked");
        using var dataChannel = environment.Channel(environment.DataPort, gameCertificate,
            "data-server");
        var rounds = new RoundStore.RoundStoreClient(dataChannel);
        var admission = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-" + roomId, TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = bindingId, ParticipantId = streamerId, Role = "Host" }
            }
        });
        Assert.Equal("Preparing", admission.Status);
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate,
            "game-server");
        var prepared = await new GameHost.GameHostClient(hostChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
                OperationId = "run-" + roomId, DurationTicks = 40, RandomSeed = 999, TickMs = 25,
                FailAfterTick = 2,
                Participants =
                {
                    new GameParticipantProto { BindingId = bindingId, ParticipantId = streamerId, Faction = "red" }
                }
            });
        Assert.Equal("Running", prepared.Status);
        return owner.Epoch;
    }

    /// <summary>The M4-4 edge cluster: the M4-3 topology plus a game host that names its
    /// instance and self-reports it — the coordinator's directory writes "game-1" into every

    internal static async Task<(int WssPort, DevelopmentPlatformSimulator Simulator)> StartEdgeClusterAsync(
        RedesignM0HostTests.HostEnvironment environment, int roundDurationTicks = 40)
    {
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var gamePort = environment.GamePort;
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-1=https://localhost:{gamePort}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_ROUND_DURATION_TICKS"] = roundDurationTicks.ToString(CultureInfo.InvariantCulture)
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m4-room"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate,
            "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m4-room" });
        }
        var streamerPort = environment.StreamerPort;
        await environment.StartAsync("Chorus.StreamerServer", streamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}""",
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}"
            });
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            },
            clientTenant: "tenant-a", instanceId: "game-1");
        var wssPort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.StreamerGateway",
            RedesignM0HostTests.HostEnvironment.FreePort(), "streamer-gateway",
            new Dictionary<string, string>
            {
                ["CHORUS_WSS_PORT"] = wssPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}""",
                ["CHORUS_STREAMER_ENDPOINT"] = $"https://localhost:{streamerPort}",
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            });
        return (wssPort, new DevelopmentPlatformSimulator(simulatorKey));
    }
}
