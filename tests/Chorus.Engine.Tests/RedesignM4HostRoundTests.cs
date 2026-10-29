using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Chorus.Redesign.StreamerGateway.Messages;
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
        var suspended = await resumed.ExpectAsync(EdgeTypes.RoomSuspended, TimeSpan.FromSeconds(30));
        Assert.Equal(epoch, suspended.GetProperty("recoveryAttempt").GetInt64());
    }

    // ---- helpers ----

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
            if (frame.GetProperty("type").GetString() != EdgeTypes.Delta) continue;
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

    private static async Task<(int WssPort, DevelopmentPlatformSimulator Simulator)> StartEdgeClusterAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var gamePort = environment.GamePort;
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-1=https://localhost:{gamePort}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m4-room"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate,
            "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m4-room" });
        }
        var streamerPort = RedesignM0HostTests.HostEnvironment.FreePort();
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
        await environment.StartAsync("Chorus.Redesign.StreamerGateway",
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
