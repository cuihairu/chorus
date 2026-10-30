using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The frame-commit pending contract under real fault injection (FIX-FRAME-04/05): a real
/// room actor committing to a real data server through a relaying proxy that can drop a
/// reply after the store committed it, or hold one while new input arrives. The database
/// carries the verdict — one frame per tick, one consumption per input — and a real sync
/// subscriber carries the client-side chain.
/// </summary>
public sealed class RedesignFrameFaultHostTests
{
    private const int DurationTicks = 40;

    [Fact]
    public async Task AReplyLostAfterTheStoreCommitsStillRunsToTheSameResult()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var cluster = await BootAsync(environment);
        const string roomId = "room-fc04";

        var (epoch, host, guest) = await OpenRoundAsync(environment, cluster, roomId);

        // FIX-FRAME-04: the first three sends of one tick's commit are relayed to the
        // store — each really commits or replays its stored receipt — but their replies
        // are dropped. The actor must resend the identical request until a verdict
        // arrives, then advance that tick exactly once.
        var target = $"{roomId}/tick-12";
        var dropped = 0;
        cluster.Faults.Rule = request =>
        {
            if (request.CommitId == target && dropped < 3)
            {
                dropped++;
                return FrameFaultPlan.DropReplyAfterRelay();
            }
            return FrameFaultPlan.Relay();
        };

        using var gameHostCertificate = environment.Certificate("@cluster", "control-server", "control-fc04");
        using var gameHostChannel = environment.Channel(cluster.GamePort, gameHostCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(gameHostChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch,
                OperationId = "fc04-prepare", DurationTicks = DurationTicks, RandomSeed = 777, TickMs = 25,
                Participants =
                {
                    new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                    new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
                }
            });
        Assert.Equal("Running", prepared.Status);

        using var syncCertificate = environment.Certificate("tenant-a", "streamer-server", "sync-fc04");
        using var syncChannel = environment.Channel(cluster.GamePort, syncCertificate, "game-server");
        using var subscriber = new RoundSync.RoundSyncClient(syncChannel).Subscribe(new SubscribeRequest
        {
            RoomId = roomId, GameId = "game-a", Epoch = epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 1
        }, deadline: DateTime.UtcNow.AddMinutes(4));
        var messages = new List<SyncMessage>();
        var reading = Task.Run(async () =>
        {
            await foreach (var message in subscriber.ResponseStream.ReadAllAsync())
                lock (messages) messages.Add(message);
        });

        // One client command mid-round: its consumption and effect must appear exactly
        // once no matter how many times the frame's replies were lost.
        var pushed = await PushGiftAsync(environment, host, roomId, epoch, "cmd-fc04-gift");

        await reading.WaitAsync(TimeSpan.FromSeconds(150));
        AssertResultChain(messages, roomId);

        // The dropped tick was sent four times — commit plus three resent duplicates —
        // all canonically identical, and the store replayed its stored receipt for every
        // resend after the first (FIX-FRAME-01 verbatim resend, FIX-FRAME-02 single step).
        var targetSends = cluster.Faults.CommitsFor(target);
        Assert.Equal(4, targetSends.Count);
        Assert.All(targetSends, send => Assert.Equal(targetSends[0].Request, send.Request));
        Assert.False(targetSends[0].Receipt.Duplicate);
        Assert.All(targetSends.Skip(1), send => Assert.True(send.Receipt.Duplicate));
        Assert.Equal(12, targetSends[0].Receipt.Tick);

        // The database kept exactly one frame per tick and the round ran to its end.
        var frames = await ReadFramesAsync(environment, roomId);
        AssertCommittedFrames(frames);

        // The pushed command was consumed exactly once: one command receipt on the
        // stream, one committed frame carrying its input event.
        var carrying = messages.Where(message => message.KindCase == SyncMessage.KindOneofCase.Delta
            && message.Delta.Commands.Any(command => command.CommandId == pushed.CommandId)).ToList();
        Assert.Single(carrying);
        var carryingTick = carrying[0].Delta.Tick;
        var eventId = Assert.Single(carrying[0].Delta.InputEventIds);
        var consumingFrames = frames.Where(frame => frame.InputRefs.Contains(eventId)).ToList();
        Assert.Single(consumingFrames);
        Assert.Equal(carryingTick, consumingFrames[0].Tick);

        // The stream's frozen result is the database's committed result.
        var result = messages.Single(message => message.KindCase == SyncMessage.KindOneofCase.RoundResult);
        var committed = await ReadResultAsync(environment, roomId);
        Assert.Equal(DurationTicks, committed.FinalTick);
        AssertSameRanking(result.RoundResult.ResultJson, committed.ResultJson);
        Assert.NotEqual("Running", await RoundStatusAsync(environment, roomId));
        await cluster.Proxy.StopAsync();
    }

    [Fact]
    public async Task NewInputArrivingOnAHeldCommitOnlyEntersLaterFrames()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var cluster = await BootAsync(environment);
        const string roomId = "room-fc05";

        var (epoch, host, guest) = await OpenRoundAsync(environment, cluster, roomId);

        // FIX-FRAME-05: tick 12's commit is relayed — the store commits it — and then
        // held without a reply while a second input reaches the room's inbox. The actor
        // is stuck on the unconfirmed frame: it may not read the new input into the
        // original commit id. Releasing the hold drops the reply, so the resend meets
        // the store's idempotency, and the new input may only enter a later frame.
        var target = $"{roomId}/tick-12";
        var heldOnce = false;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Faults.Rule = request =>
        {
            if (request.CommitId == target && !heldOnce)
            {
                heldOnce = true;
                return FrameFaultPlan.HoldReplyAfterRelay(hold);
            }
            return FrameFaultPlan.Relay();
        };

        using var gameHostCertificate = environment.Certificate("@cluster", "control-server", "control-fc05");
        using var gameHostChannel = environment.Channel(cluster.GamePort, gameHostCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(gameHostChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch,
                OperationId = "fc05-prepare", DurationTicks = DurationTicks, RandomSeed = 777, TickMs = 25,
                Participants =
                {
                    new GameParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red" },
                    new GameParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue" }
                }
            });
        Assert.Equal("Running", prepared.Status);

        using var syncCertificate = environment.Certificate("tenant-a", "streamer-server", "sync-fc05");
        using var syncChannel = environment.Channel(cluster.GamePort, syncCertificate, "game-server");
        using var subscriber = new RoundSync.RoundSyncClient(syncChannel).Subscribe(new SubscribeRequest
        {
            RoomId = roomId, GameId = "game-a", Epoch = epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 1
        }, deadline: DateTime.UtcNow.AddMinutes(4));
        var messages = new List<SyncMessage>();
        var reading = Task.Run(async () =>
        {
            await foreach (var message in subscriber.ResponseStream.ReadAllAsync())
                lock (messages) messages.Add(message);
        });

        // The held frame relayed (the store committed tick 12) before the reply froze.
        Assert.True(await WaitForAsync(() => Task.FromResult(cluster.Faults.CommitsFor(target).Count == 1)),
            "The proxy never relayed the held commit.");

        // While the frame is unconfirmed, a second input is accepted into the room.
        var lateInput = await PushGiftAsync(environment, host, roomId, epoch, "cmd-fc05-late");
        hold.SetResult();

        await reading.WaitAsync(TimeSpan.FromSeconds(150));
        AssertResultChain(messages, roomId);

        // The pending frame was resent verbatim — the new input never mixed into the
        // original commit id — and the store answered the resend with the duplicate
        // receipt of the commit the hold had already landed.
        var targetSends = cluster.Faults.CommitsFor(target);
        Assert.Equal(2, targetSends.Count);
        Assert.Equal(targetSends[0].Request, targetSends[1].Request);
        Assert.False(targetSends[0].Receipt.Duplicate);
        Assert.True(targetSends[1].Receipt.Duplicate);

        // The late input was consumed exactly once, and only by a frame after the held one.
        var frames = await ReadFramesAsync(environment, roomId);
        AssertCommittedFrames(frames);
        var carrying = messages.Where(message => message.KindCase == SyncMessage.KindOneofCase.Delta
            && message.Delta.Commands.Any(command => command.CommandId == lateInput.CommandId)).ToList();
        Assert.Single(carrying);
        var eventId = Assert.Single(carrying[0].Delta.InputEventIds);
        var consuming = frames.Single(frame => frame.InputRefs.Contains(eventId));
        Assert.True(consuming.Tick > 12, $"The late input consumed at tick {consuming.Tick} must follow the held tick 12.");
        Assert.DoesNotContain(eventId, frames.Single(frame => frame.Tick == 12).InputRefs);

        var result = messages.Single(message => message.KindCase == SyncMessage.KindOneofCase.RoundResult);
        var committed = await ReadResultAsync(environment, roomId);
        AssertSameRanking(result.RoundResult.ResultJson, committed.ResultJson);
        Assert.NotEqual("Running", await RoundStatusAsync(environment, roomId));
        await cluster.Proxy.StopAsync();
    }

    /// <summary>Boots control, data, a simulator-backed streamer server and one game host
    /// whose data plane goes through the fault-injecting proxy.</summary>
    private static async Task<FaultCluster> BootAsync(RedesignM0HostTests.HostEnvironment environment)
    {
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"game-1=https://localhost:{gamePort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-fc"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-fc" });
        }

        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}""",
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}"
            });

        var (proxyPort, faults, proxy) = await StartProxyAsync(environment);

        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = proxyPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_CONTROL_ENDPOINT"] = $"https://localhost:{environment.ControlPort}",
                ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
                ["CHORUS_SLOT_ID"] = "node-a/game-server",
                ["CHORUS_REPORT_INTERVAL_MS"] = "500"
            }, clientTenant: "tenant-a", instanceId: "game-1");
        return new FaultCluster(faults, proxy, gamePort, new DevelopmentPlatformSimulator(simulatorKey));
    }

    /// <summary>Claims the room, starts the round on the data plane and mounts its two
    /// participants as live streamer sessions.</summary>
    private static async Task<(long Epoch, (string StreamerId, string SessionId, string BindingId) Host,
        (string StreamerId, string SessionId, string BindingId) Guest)> OpenRoundAsync(
        RedesignM0HostTests.HostEnvironment environment, FaultCluster cluster, string roomId)
    {
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-open");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-1", LeaseSeconds = 15
        });
        Assert.True(claimed.Found);

        var simulator = cluster.Simulator;
        var host = await MountAsync(environment, simulator, "open-fc-a");
        var guest = await MountAsync(environment, simulator, "open-fc-b");

        using var roundCertificate = environment.Certificate("tenant-a", "game-server", "rounds-fc");
        using var roundChannel = environment.Channel(environment.DataPort, roundCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(roundChannel);
        var started = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-1", Epoch = claimed.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = roomId + "/start", TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        Assert.Equal("Preparing", started.Status);
        return (claimed.Epoch, host, guest);
    }

    private static async Task<string> RoundStatusAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId)
    {
        using var certificate = environment.Certificate("tenant-a", "game-server", "status-fc");
        using var channel = environment.Channel(environment.DataPort, certificate, "data-server");
        var reply = await new RoundStore.RoundStoreClient(channel).GetRoundAsync(
            new GetRoundRequest { RoomId = roomId, GameId = "game-a" },
            deadline: DateTime.UtcNow.AddSeconds(30));
        return reply.Status;
    }

    private static async Task<ClientInputReply> PushGiftAsync(
        RedesignM0HostTests.HostEnvironment environment,
        (string StreamerId, string SessionId, string BindingId) who, string roomId, long epoch,
        string commandId)
    {
        using var certificate = environment.Certificate("tenant-a", "streamer-gateway", "input-fc");
        using var channel = environment.Channel(environment.DataPort, certificate, "data-server");
        var input = new RoundInput.RoundInputClient(channel);
        return await input.AcceptClientInputAsync(new ClientInputRequest
        {
            SessionId = who.SessionId, RoomId = roomId, RoomEpoch = epoch, GameId = "game-a",
            CommandId = commandId, PayloadJson = """{"kind":"gift","amount":7}"""
        }, deadline: DateTime.UtcNow.AddSeconds(30));
    }

    /// <summary>Asserts the client-side chain: every delta chains on the previous applied
    /// version, ticks only move forward one step, a snapshot re-baselines, and the stream
    /// ends with the frozen result at the committed head.</summary>
    private static void AssertResultChain(List<SyncMessage> messages, string roomId)
    {
        SyncMessage? result = null;
        var chainedVersion = 1L;
        var chainedTick = 0L;
        var chained = false;
        foreach (var message in messages)
        {
            switch (message.KindCase)
            {
                case SyncMessage.KindOneofCase.Snapshot:
                    // A snapshot re-baselines like any real one: the chain restarts at
                    // the version the snapshot names and later deltas chain on from it.
                    chainedVersion = message.Snapshot.StateVersion;
                    chainedTick = message.Snapshot.Tick;
                    chained = true;
                    break;
                case SyncMessage.KindOneofCase.Delta:
                    if (chained)
                    {
                        Assert.Equal(chainedVersion, message.Delta.BaseVersion);
                        Assert.Equal(chainedTick + 1, message.Delta.Tick);
                    }
                    Assert.Equal(message.Delta.BaseVersion + 1, message.Delta.StateVersion);
                    chainedVersion = message.Delta.StateVersion;
                    chainedTick = message.Delta.Tick;
                    chained = true;
                    break;
                case SyncMessage.KindOneofCase.RoundResult:
                    result = message;
                    break;
                default:
                    Assert.Fail($"Unexpected sync message {message.KindCase} for {roomId}.");
                    break;
            }
        }
        Assert.NotNull(result);
        Assert.Equal(chainedTick, result!.RoundResult.Tick);
        Assert.Equal(chainedVersion, result.RoundResult.StateVersion);
    }

    /// <summary>The database kept exactly one frame per tick, from the first tick to the
    /// configured duration — no duplicate ever landed, no tick was skipped.</summary>
    private static void AssertCommittedFrames(List<FrameRow> frames)
    {
        Assert.Equal(DurationTicks, frames.Count);
        Assert.Equal(DurationTicks, frames.Select(frame => frame.Tick).Distinct().Count());
        Assert.Equal(1, frames[0].Tick);
        Assert.Equal(DurationTicks, frames[^1].Tick);
    }

    private sealed record FrameRow(long Tick, string CommitId, List<string> InputRefs);

    private static async Task<List<FrameRow>> ReadFramesAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId)
    {
        var frames = new List<FrameRow>();
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT tick_id,commit_id,input_refs FROM game.frame WHERE room_id=@room ORDER BY tick_id",
            connection);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var refs = new List<string>();
            using (var document = JsonDocument.Parse(reader.GetString(2)))
                foreach (var entry in document.RootElement.EnumerateArray())
                    refs.Add(entry.GetString()!);
            frames.Add(new FrameRow(reader.GetInt64(0), reader.GetString(1), refs));
        }
        return frames;
    }

    private sealed record CommittedResult(long FinalTick, string ResultJson);

    private static async Task<CommittedResult> ReadResultAsync(
        RedesignM0HostTests.HostEnvironment environment, string roomId)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT final_frame,result FROM game.result WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The room never committed a result.");
        return new CommittedResult(reader.GetInt64(0), reader.GetString(1));
    }

    /// <summary>The database stores the committed result wrapped and normalized by the
    /// store; the client sees the module's canonical json. Both must agree on the
    /// ranking they carry.</summary>
    private static void AssertSameRanking(string streamJson, string committedJson)
    {
        using var stream = JsonDocument.Parse(streamJson);
        using var committed = JsonDocument.Parse(committedJson);
        var streamRanking = stream.RootElement.GetProperty("ranking");
        var committedRanking = committed.RootElement.GetProperty("ranking");
        Assert.Equal(streamRanking.GetArrayLength(), committedRanking.GetArrayLength());
        for (var index = 0; index < streamRanking.GetArrayLength(); index++)
        {
            Assert.Equal(streamRanking[index].GetProperty("faction").GetString(),
                committedRanking[index].GetProperty("faction").GetString());
            Assert.Equal(streamRanking[index].GetProperty("score").GetInt64(),
                committedRanking[index].GetProperty("score").GetInt64());
        }
    }

    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-fc-" + openId;
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
        return (login.StreamerId, login.SessionId, mounted.BindingId);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(200);
        }
        return await probe();
    }

    /// <summary>The relay proxy's server side: a data-server-shaped TLS endpoint whose
    /// gRPC services forward to the real data server, with CommitFrame scripted.</summary>
    private static async Task<(int Port, FrameFaultInjector Faults, WebApplication Proxy)> StartProxyAsync(
        RedesignM0HostTests.HostEnvironment environment)
    {
        var port = RedesignM0HostTests.HostEnvironment.FreePort();
        // The proxy owns its certificates for its whole lifetime — no using here: Kestrel
        // and the upstream handler keep using them long after this method returns.
        var serverCertificate = environment.Certificate("@cluster", "data-server", "frame-fault-proxy");
        var upstreamCertificate = environment.Certificate("tenant-a", "game-server", "frame-fault-upstream");
        var upstreamChannel = environment.Channel(environment.DataPort, upstreamCertificate, "data-server");
        var faults = new FrameFaultInjector();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenLocalhost(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(serverCertificate);
            });
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(new FrameStore.FrameStoreClient(upstreamChannel));
        builder.Services.AddSingleton(new RoundStore.RoundStoreClient(upstreamChannel));
        builder.Services.AddSingleton(new Settlement.SettlementClient(upstreamChannel));
        builder.Services.AddSingleton(faults);
        var proxy = builder.Build();
        proxy.MapGrpcService<FrameRelayService>();
        proxy.MapGrpcService<RoundRelayService>();
        proxy.MapGrpcService<SettlementRelayService>();
        await proxy.StartAsync();
        return (port, faults, proxy);
    }

    private sealed record FaultCluster(FrameFaultInjector Faults, WebApplication Proxy, int GamePort,
        DevelopmentPlatformSimulator Simulator);

    private sealed record FrameFaultPlan(bool DropReply, TaskCompletionSource? HoldUntil)
    {
        public static FrameFaultPlan Relay() => new(false, null);
        public static FrameFaultPlan DropReplyAfterRelay() => new(true, null);
        public static FrameFaultPlan HoldReplyAfterRelay(TaskCompletionSource hold) => new(false, hold);
    }

    private sealed record CommitObservation(string CommitId, byte[] Request, FrameReceiptReply Receipt);

    /// <summary>
    /// Relays CommitFrame to the real store and then applies the scripted fault to the
    /// reply. The relay always happens first: a dropped reply is a reply loss after a
    /// real commit, never a commit loss — exactly the unknown outcome the actor must
    /// survive. Every relay is recorded with its canonical request bytes and the store's
    /// receipt, so tests can prove verbatim resends and duplicate verdicts.
    /// </summary>
    private sealed class FrameFaultInjector
    {
        private readonly object _gate = new();
        private readonly List<CommitObservation> _commits = new();
        public Func<CommitFrameRequest, FrameFaultPlan>? Rule { get; set; }

        public async Task<FrameReceiptReply> RelayAsync(CommitFrameRequest request,
            FrameStore.FrameStoreClient upstream, CancellationToken cancellationToken)
        {
            FrameFaultPlan plan;
            lock (_gate) plan = Rule?.Invoke(request) ?? FrameFaultPlan.Relay();
            var reply = await upstream.CommitFrameAsync(request, deadline: DateTime.UtcNow.AddSeconds(20),
                cancellationToken: cancellationToken);
            lock (_gate)
                _commits.Add(new CommitObservation(request.CommitId, request.ToByteArray(), reply));
            if (plan.HoldUntil is not null)
                await plan.HoldUntil.Task.WaitAsync(cancellationToken);
            if (plan.DropReply || plan.HoldUntil is not null)
                throw new RpcException(new Status(StatusCode.Unavailable,
                    "the proxy dropped the reply after the store committed"));
            return reply;
        }

        public List<CommitObservation> CommitsFor(string commitId)
        {
            lock (_gate) return _commits.Where(observation => observation.CommitId == commitId).ToList();
        }
    }

    private static DateTime RelayDeadline() => DateTime.UtcNow.AddSeconds(20);

    private sealed class FrameRelayService(
        FrameStore.FrameStoreClient upstream, FrameFaultInjector faults)
        : FrameStore.FrameStoreBase
    {
        public override Task<ReadyReply> Ready(Empty request, ServerCallContext context) =>
            upstream.ReadyAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<ReadInboxReply> ReadInbox(ReadInboxRequest request, ServerCallContext context) =>
            upstream.ReadInboxAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<FrameReceiptReply> CommitFrame(CommitFrameRequest request,
            ServerCallContext context) =>
            faults.RelayAsync(request, upstream, context.CancellationToken);

        public override Task<SnapshotReceiptReply> PutSnapshot(PutSnapshotRequest request,
            ServerCallContext context) =>
            upstream.PutSnapshotAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<LoadRecoveryReply> LoadRecovery(LoadRecoveryRequest request,
            ServerCallContext context) =>
            upstream.LoadRecoveryAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;
    }

    private sealed class RoundRelayService(RoundStore.RoundStoreClient upstream) : RoundStore.RoundStoreBase
    {
        public override Task<ReadyReply> Ready(Empty request, ServerCallContext context) =>
            upstream.ReadyAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<RoundAdmissionReply> StartRound(StartRoundRequest request,
            ServerCallContext context) =>
            upstream.StartRoundAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<RoundAdmissionReply> AbortRound(AbortRoundRequest request,
            ServerCallContext context) =>
            upstream.AbortRoundAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<RoundStatusReply> GetRound(GetRoundRequest request, ServerCallContext context) =>
            upstream.GetRoundAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<RoundAdmissionReply> PrepareRound(PrepareRoundRequest request,
            ServerCallContext context) =>
            upstream.PrepareRoundAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<RoundAdmissionReply> ActivateRound(ActivateRoundRequest request,
            ServerCallContext context) =>
            upstream.ActivateRoundAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<RoundAdmissionReply> SuspendRound(SuspendRoundRequest request,
            ServerCallContext context) =>
            upstream.SuspendRoundAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;
    }

    private sealed class SettlementRelayService(Settlement.SettlementClient upstream)
        : Settlement.SettlementBase
    {
        public override Task<ReadyReply> Ready(Empty request, ServerCallContext context) =>
            upstream.ReadyAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<CommitResultReply> CommitResult(CommitResultRequest request,
            ServerCallContext context) =>
            upstream.CommitResultAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;

        public override Task<FinalizeRoundReply> FinalizeRound(FinalizeRoundRequest request,
            ServerCallContext context) =>
            upstream.FinalizeRoundAsync(request, deadline: RelayDeadline(), cancellationToken: context.CancellationToken).ResponseAsync;
    }
}
