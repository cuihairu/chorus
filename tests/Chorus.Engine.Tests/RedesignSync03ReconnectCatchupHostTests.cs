using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Chorus.Client;
using Chorus.ExampleGame;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using RoutingService = Chorus.Rpc.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-03 acceptance (验证断线与迁移追赶): a client that disconnects mid-round while
/// the authority migrates to a second instance is answered, on its stale-epoch re-subscribe,
/// with a resync pointer; at the new epoch it receives the head snapshot, and because its
/// bounded queue overflows again while it installs that baseline it is re-baselined with a
/// newer snapshot before draining gapless deltas to the frozen result. No routed gift is ever
/// consumed twice (the store records each event once, the client never applies one twice), the
/// reconnect resumes the same round rather than a new battle, and a terminal reconnect returns
/// the frozen result the server settled — matching the committed final frame.
/// </summary>
public sealed class RedesignSync03ReconnectCatchupHostTests
{
    private const int SigStop = 19;
    private const int SigCont = 18;
    private const string Room = "room-sync-03";
    private const int FinalTick = 600;

    [Fact]
    public async Task ADisconnectedClientReconnectsAcrossEpochAndCatchesUpWithoutReplays()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var gamePortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var gamePortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // The control plane seeds both hosts and runs the ownership registrar short so the
        // silence of a frozen holder lapses its lease within seconds (M3Takeover pattern).
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] =
                    $"game-a=https://localhost:{gamePortA},game-b=https://localhost:{gamePortB}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "6",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "3"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });

        await environment.StartAsync("Chorus.GameServer", gamePortA, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortA, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-a");
        await environment.StartAsync("Chorus.GameServer", gamePortB, "game-server",
            GameEnvironment(environment.DataPort, controlEndpoint, gamePortB, "node-a/game-server#2"),
            clientTenant: "tenant-a", instanceId: "game-b");
        var hostA = environment.HostProcesses.Single(entry => entry.Instance == "game-a").Process;

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-sync-03");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "rounds-sync-03");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);

        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-sync-03"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-sync-03" });
        }
        var registered = await WaitForAsync(async () =>
        {
            var snapshot = await routing.GetRoutingSnapshotAsync(new Empty());
            return snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-a"
                && instance.Endpoint.Length > 0)
                && snapshot.Instances.Any(instance => instance.ProcessInstanceId == "game-b"
                    && instance.Endpoint.Length > 0);
        }, TimeSpan.FromSeconds(30));
        Assert.True(registered, "The game hosts did not self-register into the routing snapshot.");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, _, hostBinding) = await MountAsync(environment, simulator, "open-sync-03-a", "live-sync-03-a");
        var (guest, _, guestBinding) = await MountAsync(environment, simulator, "open-sync-03-b", "live-sync-03-b");

        var claimed = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = Room,
            OwnerInstanceId = "game-a", LeaseSeconds = 10
        });
        Assert.True(claimed.Found);
        Assert.Equal("game-a", claimed.OwnerInstanceId);
        var epoch = claimed.Epoch;

        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-a", Epoch = epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1",
            OperationId = "start-sync-03", TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest, Role = "Guest" }
            }
        });

        // The gifts are routed while the room is still Preparing so they sit in the inbox and
        // are consumed by the first running ticks (M2 pattern) — before the disconnect.
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-sync-03" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-sync-03-a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-sync-03-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-sync-03-b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-sync-03-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch);
        await new RoundInput.RoundInputClient(gameChannel).RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-sync-03", MaxEvents = 50
        });

        using var hostChannelA = environment.Channel(gamePortA, controlCertificate, "game-server");
        Assert.Equal("Running", (await new GameHost.GameHostClient(hostChannelA).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = Room, GameId = "game-a", OwnerInstanceId = "game-a", Epoch = epoch,
                OperationId = "run-sync-03", DurationTicks = FinalTick, RandomSeed = 4242, TickMs = 5,
                Participants =
                {
                    new GameParticipantProto { BindingId = hostBinding, ParticipantId = host, Faction = "red" },
                    new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest, Faction = "blue" }
                }
            })).Status);

        // --- 断线: subscribe at the current head (the tick-0 snapshot is already committed, so
        // the baseline is served), consume the gift deltas, then drop the stream mid-round. ---
        using var edgeCertificate = environment.Certificate("tenant-a", "streamer-server", "edge-sync-03");
        using var edgeChannelA = environment.Channel(gamePortA, edgeCertificate, "game-server");
        var syncA = new RoundSync.RoundSyncClient(edgeChannelA);
        var preDisconnect = await ReadPreDisconnectAsync(syncA.Subscribe(SubscribeRequest(epoch)), epoch);
        Assert.True(preDisconnect.Applier.Status.HasBaseline);

        // --- 迁移: A goes silent so its lease lapses; the control plane hands the room to B at
        // the bumped epoch and B recovers from the pinned head and resumes ticking. ---
        _ = kill(hostA.Id, SigStop);
        var leaseExpired = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = Room
            });
            return ownership.Found && ownership.LeaseUntilUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }, TimeSpan.FromSeconds(40));
        Assert.True(leaseExpired, "The frozen holder's lease never expired.");
        var transferred = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = Room,
            ToInstanceId = "game-b", LeaseSeconds = 15, OperationId = "transfer-sync-03"
        });
        Assert.Equal("game-b", transferred.OwnerInstanceId);
        Assert.Equal("Recovering", transferred.Status);
        var newEpoch = transferred.Epoch;
        Assert.Equal(epoch + 1, newEpoch);
        var recovered = await takeover.TakeoverRoomAsync(new TakeoverRoomRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = Room,
            ToInstanceId = "game-b", LeaseSeconds = 15, TickMs = 5
        });
        Assert.True(recovered.Recovered);
        Assert.Equal("game-b", recovered.OwnerInstanceId);
        Assert.Equal(newEpoch, recovered.Epoch);
        Assert.Equal("Running", recovered.Status);

        // --- 跨 epoch 重订阅: the client re-resolves the route to the new owner and asks with
        // the stale epoch; the server answers a resync pointer (old flow aborted, new baseline
        // required), not a stream it could chain onto. ---
        using var edgeChannelB = environment.Channel(gamePortB, edgeCertificate, "game-server");
        var syncB = new RoundSync.RoundSyncClient(edgeChannelB);
        using var staleCall = syncB.Subscribe(SubscribeRequest(epoch));
        var stale = await ReadFirstAsync(staleCall);
        Assert.Equal(SyncMessage.KindOneofCase.ResyncRequired, stale.KindCase);
        Assert.Equal(newEpoch, stale.ResyncRequired.CurrentEpoch);
        var staleDecision = preDisconnect.Applier.Decide(stale);
        Assert.Equal(BaselineDecisionKind.RebaselineRequired, staleDecision.Kind);
        Assert.Equal(newEpoch, staleDecision.TargetEpoch);

        // --- 重连安装快照期间溢出: the fresh baseline at the new epoch arrives as the head
        // snapshot; the client then goes silent while it installs, its bounded queue overflows
        // again, and the server re-baselines it with a newer snapshot before it drains to the
        // frozen result — gapless deltas on the new chain, one winner, no second battle. ---
        using var freshCall = syncB.Subscribe(SubscribeRequest(newEpoch));
        var firstSnapshot = await ReadFirstAsync(freshCall);
        Assert.Equal(SyncMessage.KindOneofCase.Snapshot, firstSnapshot.KindCase);
        var finished = await WaitForAsync(async () =>
            (await rounds.GetRoundAsync(new GetRoundRequest { RoomId = Room, GameId = "game-a" })).Status
                != "Running", TimeSpan.FromSeconds(300));
        Assert.True(finished, "The migrated room never finished after the takeover.");
        var catchup = await DrainToResultAsync(freshCall);
        var chain = ApplyChain(catchup, Room, newEpoch, firstSnapshot);
        Assert.True(chain.Snapshots >= 1,
            $"an install-time overflow must re-baseline the client with a newer snapshot, saw {chain.Snapshots}");
        var reBaseline = catchup.First(message => message.KindCase == SyncMessage.KindOneofCase.Snapshot);
        Assert.True(reBaseline.Snapshot.StateVersion > firstSnapshot.Snapshot.StateVersion,
            "the re-baseline snapshot must be newer than the one that overflowed the client");
        Assert.Equal(FinalTick, chain.FinalTick);
        Assert.Equal(FinalTick, chain.FinalResult.Tick);
        Assert.Equal("red", WinnerOf(chain.FinalResult));
        Assert.True(chain.Applier.Status.Terminal);

        // --- 终局后重连: a fresh subscribe after the freeze returns the same frozen result
        // with no deltas — the battle is not restarted, the settled answer is stable. ---
        using var terminalCall = syncB.Subscribe(SubscribeRequest(newEpoch));
        var terminal = await DrainToResultAsync(terminalCall);
        Assert.Equal(SyncMessage.KindOneofCase.Snapshot, terminal[0].KindCase);
        Assert.Equal(FinalTick, terminal[0].Snapshot.Tick);
        Assert.DoesNotContain(terminal, message => message.KindCase == SyncMessage.KindOneofCase.Delta);
        Assert.Equal(chain.FinalResult.ResultJson, terminal[^1].RoundResult.ResultJson);
        Assert.Equal(chain.FinalVersion, terminal[0].Snapshot.StateVersion);

        // --- 验收: 不重演奖励 + 不误重新匹配 + 最终结果与服务端一致. ---
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            var consumed = await ConsumedEventCountsAsync(connection, Room);
            Assert.True(consumed.Count >= 2, "the routed gifts were never consumed by a frame.");
            Assert.All(consumed, entry => Assert.Equal(1, entry.Value));
            var appliedEvents = preDisconnect.EventIds.Concat(chain.EventIds).ToList();
            Assert.Equal(appliedEvents.Distinct(StringComparer.Ordinal).Count(), appliedEvents.Count);
            Assert.Equal(Convert.ToHexString(chain.FinalHash),
                Convert.ToHexString(await FrameHashAsync(connection, Room, FinalTick)));
            var frozenModule = new ExampleGameModule();
            var frozenState = frozenModule.Restore(terminal[0].Snapshot.StatePayload.ToByteArray(),
                terminal[0].Snapshot.StateSchemaVersion);
            Assert.Equal(Convert.ToHexString(chain.FinalHash),
                Convert.ToHexString(frozenModule.ComputeStateHash(frozenState)));
        }

        _ = kill(hostA.Id, SigCont);
    }

    private static SubscribeRequest SubscribeRequest(long epoch) => new()
    {
        RoomId = Room, GameId = "game-a", Epoch = epoch,
        LastAppliedTick = 0, LastAppliedStateVersion = 0
    };

    /// <summary>
    /// Reads the pre-disconnect segment: the head snapshot establishes the baseline, then the
    /// first few deltas (the gift ticks) are consumed before the client drops the stream.
    /// </summary>
    private static async Task<PreDisconnect> ReadPreDisconnectAsync(
        Grpc.Core.AsyncServerStreamingCall<SyncMessage> call, long epoch)
    {
        var applier = new BaselineApplier(Room, epoch, DeltaContract.Full);
        var eventIds = new List<string>();
        var appliedTick = -1L;
        while (await call.ResponseStream.MoveNext())
        {
            var message = call.ResponseStream.Current;
            var decision = applier.Decide(message);
            switch (message.KindCase)
            {
                case SyncMessage.KindOneofCase.Snapshot:
                    Assert.Equal(BaselineDecisionKind.ApplySnapshot, decision.Kind);
                    appliedTick = message.Snapshot.Tick;
                    break;
                case SyncMessage.KindOneofCase.Delta:
                    Assert.Equal(BaselineDecisionKind.ApplyDelta, decision.Kind);
                    eventIds.AddRange(message.Delta.InputEventIds);
                    appliedTick = message.Delta.Tick;
                    break;
                case SyncMessage.KindOneofCase.RoundResult:
                    Assert.Equal(BaselineDecisionKind.ApplyResult, decision.Kind);
                    appliedTick = message.RoundResult.Tick;
                    break;
                default:
                    Assert.Fail($"Unexpected pre-disconnect message {message.KindCase}.");
                    break;
            }

            if (appliedTick >= 3) break;
        }

        return new PreDisconnect(applier, eventIds);
    }

    /// <summary>Reads a single sync message.</summary>
    private static async Task<SyncMessage> ReadFirstAsync(
        Grpc.Core.AsyncServerStreamingCall<SyncMessage> call)
    {
        Assert.True(await call.ResponseStream.MoveNext(), "the sync stream ended unexpectedly.");
        return call.ResponseStream.Current;
    }

    /// <summary>Reads a sync stream to the frozen result and returns the messages.</summary>
    private static async Task<List<SyncMessage>> DrainToResultAsync(
        Grpc.Core.AsyncServerStreamingCall<SyncMessage> call)
    {
        var messages = new List<SyncMessage>();
        while (await call.ResponseStream.MoveNext())
        {
            messages.Add(call.ResponseStream.Current);
            if (messages[^1].KindCase == SyncMessage.KindOneofCase.RoundResult) break;
        }

        return messages;
    }

    /// <summary>
    /// Applies the catch-up chain through a fresh baseline applier, seeded with the reconnect
    /// snapshot the client already holds (every message must be an apply — the migrated stream
    /// satisfies the client contract), tracking the gapless versions, the applied event ids,
    /// and the final state hash at the last applied frame.
    /// </summary>
    private static AppliedChain ApplyChain(List<SyncMessage> messages, string roomId, long epoch,
        SyncMessage reconnectSnapshot)
    {
        var applier = new BaselineApplier(roomId, epoch, DeltaContract.Full);
        var module = new ExampleGameModule();
        var eventIds = new List<string>();
        var applied = -1L;
        var appliedTick = -1L;
        var finalHash = Array.Empty<byte>();
        var snapshots = 0;
        RoundResultMessage? result = null;
        var reconnected = applier.Decide(reconnectSnapshot);
        Assert.Equal(BaselineDecisionKind.ApplySnapshot, reconnected.Kind);
        applied = reconnectSnapshot.Snapshot.StateVersion;
        appliedTick = reconnectSnapshot.Snapshot.Tick;
        finalHash = module.ComputeStateHash(module.Restore(reconnectSnapshot.Snapshot.StatePayload.ToByteArray(),
            reconnectSnapshot.Snapshot.StateSchemaVersion));
        foreach (var message in messages)
        {
            var decision = applier.Decide(message);
            switch (message.KindCase)
            {
                case SyncMessage.KindOneofCase.Snapshot:
                    snapshots++;
                    Assert.Equal(BaselineDecisionKind.ApplySnapshot, decision.Kind);
                    applied = message.Snapshot.StateVersion;
                    appliedTick = message.Snapshot.Tick;
                    finalHash = module.ComputeStateHash(module.Restore(message.Snapshot.StatePayload.ToByteArray(),
                        message.Snapshot.StateSchemaVersion));
                    break;
                case SyncMessage.KindOneofCase.Delta:
                    Assert.Equal(BaselineDecisionKind.ApplyDelta, decision.Kind);
                    Assert.Equal(applied, message.Delta.BaseVersion);
                    applied = message.Delta.StateVersion;
                    appliedTick = message.Delta.Tick;
                    finalHash = message.Delta.StateHash.ToByteArray();
                    eventIds.AddRange(message.Delta.InputEventIds);
                    break;
                case SyncMessage.KindOneofCase.RoundResult:
                    Assert.Equal(BaselineDecisionKind.ApplyResult, decision.Kind);
                    Assert.Equal(applied, message.RoundResult.StateVersion);
                    result = message.RoundResult;
                    break;
                default:
                    Assert.Fail($"Unexpected catch-up message {message.KindCase}.");
                    break;
            }
        }

        Assert.NotNull(result);
        return new AppliedChain(applied, appliedTick, snapshots, eventIds, finalHash, result, applier);
    }

    private static string WinnerOf(RoundResultMessage result)
    {
        using var document = JsonDocument.Parse(result.ResultJson);
        return document.RootElement.GetProperty("winner").GetString()!;
    }

    /// <summary>Counts how many committed frames reference each consumed event id.</summary>
    private static async Task<Dictionary<string, int>> ConsumedEventCountsAsync(
        NpgsqlConnection connection, string roomId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT input_refs FROM game.frame WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var ids = JsonSerializer.Deserialize<List<string>>(reader.GetString(0)) ?? new List<string>();
            foreach (var id in ids)
            {
                counts[id] = counts.TryGetValue(id, out var count) ? count + 1 : 1;
            }
        }

        return counts;
    }

    private static async Task<byte[]> FrameHashAsync(NpgsqlConnection connection, string roomId, long tick)
    {
        await using var command = new NpgsqlCommand(
            "SELECT state_hash FROM game.frame WHERE room_id=@room AND tick_id=@tick", connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("tick", tick);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static Dictionary<string, string> GameEnvironment(
        int dataPort, string controlEndpoint, int gamePort, string slotId) => new()
    {
        ["CHORUS_DATA_PORT"] = dataPort.ToString(CultureInfo.InvariantCulture),
        ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
        ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{gamePort}",
        ["CHORUS_SLOT_ID"] = slotId,
        ["CHORUS_REPORT_INTERVAL_MS"] = "500"
    };

    /// <summary>Logs a simulator streamer in through the real StreamerServer and mounts game-a.</summary>
    private static async Task<(string StreamerId, string SessionId, string BindingId)> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator,
        string openId, string liveKey)
    {
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

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(200);
        }
        return await probe();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private sealed record PreDisconnect(BaselineApplier Applier, List<string> EventIds);

    private sealed record AppliedChain(
        long FinalVersion, long FinalTick, int Snapshots, List<string> EventIds,
        byte[] FinalHash, RoundResultMessage FinalResult, BaselineApplier Applier);
}
