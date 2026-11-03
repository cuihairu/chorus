using System.Globalization;
using System.Security.Cryptography;
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
/// FIX-SYNC-02 acceptance (验证双主播同版本收敛): two subscribers at different delays
/// converge on the same state at the same version — the slow client's bounded queue
/// overflows and it is re-baselined with a head snapshot while the fast client streams
/// contiguous deltas to the frozen result without being blocked, both clients carry the
/// same state hash at every common version (delta hashes directly, snapshot hashes
/// recomputed from the payload), and every applied version is a committed frame row in
/// the store (no un-persisted frame is ever applied).
/// </summary>
public sealed class RedesignSync02DualClientConvergenceHostTests
{
    [Fact]
    public async Task TwoClientsAtDifferentDelaysConvergeOnTheSameVersionsAndPersistedState()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (simulatorKey, accessKey) = (RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var (host, hostBinding) = await MountAsync(environment, simulator, "open-sync-02-a", "live-sync-02-a");
        var (guest, guestBinding) = await MountAsync(environment, simulator, "open-sync-02-b", "live-sync-02-b");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-actor");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        // The 600-tick room is work-bound (two data-plane calls per tick) and can outrun a
        // fixed lease under co-tenant load: renew the claim so an expired lease never fences
        // the owner's frame commits out mid-round (architecture 4.2).
        using var roundClaim = await RenewableClaim.ClaimAsync(control, new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = Room,
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        });
        var owner = roundClaim.Owner;
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = Room, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-sync-02",
            Participants =
            {
                new RoundParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Role = "Host" },
                new RoundParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Role = "Guest" }
            }
        });
        using var hostChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        Assert.Equal("Running", (await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = Room, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "run-sync-02", DurationTicks = 600, RandomSeed = 4242, TickMs = 5,
            Participants =
            {
                new GameParticipantProto { BindingId = hostBinding, ParticipantId = host.StreamerId, Faction = "red" },
                new GameParticipantProto { BindingId = guestBinding, ParticipantId = guest.StreamerId, Faction = "blue" }
            }
        })).Status);
        // Subscribe both clients as early as possible (right after the room turns Running,
        // before the gifts are routed) so the fast reader captures the chain from the first
        // committed frame — the ingest/route latency must never decide how many deltas the
        // fast client sees.
        using var edgeCertificate = environment.Certificate("tenant-a", "streamer-server", "edge-1");
        using var edgeChannel = environment.Channel(environment.GamePort, edgeCertificate, "game-server");
        var sync = new RoundSync.RoundSyncClient(edgeChannel);
        using var fastCall = sync.Subscribe(SubscribeRequest(owner.Epoch));
        using var slowCall = sync.Subscribe(SubscribeRequest(owner.Epoch));

        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-1");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-sync-02" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-sync-02-a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-sync-02-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-sync-02-b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = "live-sync-02-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch);
        await new RoundInput.RoundInputClient(gameChannel).RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-sync-02", MaxEvents = 50
        });

        var fastMessages = await DrainToResultAsync(fastCall);
        Assert.Equal(SyncMessage.KindOneofCase.Snapshot, fastMessages[0].KindCase);
        Assert.Equal(600L, fastMessages[^1].RoundResult.Tick);
        Assert.Equal("red", WinnerOf(fastMessages[^1]));
        var fastChain = ApplyChain(fastMessages, Room, owner.Epoch);
        Assert.Equal(601L, fastChain.FinalVersion);
        Assert.Equal(1, fastChain.Snapshots);
        Assert.True(fastChain.Deltas >= 400, $"the fast reader saw {fastChain.Deltas} deltas");
        Assert.True(fastChain.Applier.Status.Terminal);

        // The silent reader's transport window exhausts and its 64-message queue overflows
        // while the round kept ticking; the backlog drops and a snapshot of the then-current
        // head re-baselines it. The room never waited for it — the fast chain already reached
        // the frozen result.
        var slowMessages = await DrainToResultAsync(slowCall);
        Assert.Equal("red", WinnerOf(slowMessages[^1]));
        var slowChain = ApplyChain(slowMessages, Room, owner.Epoch);
        Assert.True(slowChain.Snapshots >= 2,
            $"a lagging subscriber must be re-baselined with a snapshot, saw {slowChain.Snapshots}");
        Assert.Equal(601L, slowChain.FinalVersion);
        Assert.True(slowChain.Applier.Status.Terminal);

        // --- 相同版本状态哈希一致: at every version both clients applied, the state hash
        // is identical. The slow client's re-baseline snapshot hash is recomputed from its
        // payload and must match the fast client's delta hash at the same version (and vice
        // versa) — cross-representation convergence, not just byte-identical deltas. ---
        var commonVersions = fastChain.VersionHashes.Keys.Where(slowChain.VersionHashes.ContainsKey).ToList();
        Assert.True(commonVersions.Count > 300,
            $"both clients should share hundreds of applied versions, saw {commonVersions.Count}");
        foreach (var version in commonVersions)
        {
            Assert.Equal(fastChain.VersionHashes[version], slowChain.VersionHashes[version]);
        }

        // --- 不应用未持久帧: every applied delta corresponds to a committed frame row in
        // the store carrying the same state hash, and the slow client's re-baseline snapshot
        // (tick > 0, so a real committed frame) matches the store's frame hash too. ---
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        foreach (var (tick, hash) in fastChain.AppliedTicks.Concat(slowChain.AppliedTicks).DistinctBy(entry => entry.Tick))
        {
            Assert.Equal(Convert.ToHexString(hash), Convert.ToHexString(await FrameHashAsync(connection, Room, tick)));
        }
        foreach (var (tick, hash) in fastChain.SnapshotTicks.Concat(slowChain.SnapshotTicks)
                     .Where(entry => entry.Tick >= 1).DistinctBy(entry => entry.Tick))
        {
            Assert.Equal(Convert.ToHexString(hash), Convert.ToHexString(await FrameHashAsync(connection, Room, tick)));
        }
    }

    private const string Room = "room-sync-02";

    private static SubscribeRequest SubscribeRequest(long epoch) => new()
    {
        RoomId = Room, GameId = "game-a", Epoch = epoch,
        LastAppliedTick = 0, LastAppliedStateVersion = 0
    };

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
    /// Applies one client's stream through the FIX-SYNC-01 baseline applier (every message
    /// must be an apply — the real server stream satisfies the client contract), tracks the
    /// version→hash map (delta hashes direct, snapshot hashes recomputed from the payload)
    /// and the applied frame ticks for the persisted-state probe.
    /// </summary>
    private static AppliedChain ApplyChain(List<SyncMessage> messages, string roomId, long epoch)
    {
        var applier = new BaselineApplier(roomId, epoch, DeltaContract.Full);
        var module = new ExampleGameModule();
        var hashes = new Dictionary<long, string>();
        var appliedTicks = new List<(long Tick, byte[] Hash)>();
        var snapshotTicks = new List<(long Tick, byte[] Hash)>();
        var applied = -1L;
        var snapshots = 0;
        var deltas = 0;
        foreach (var message in messages)
        {
            var decision = applier.Decide(message);
            switch (message.KindCase)
            {
                case SyncMessage.KindOneofCase.Snapshot:
                    snapshots++;
                    Assert.Equal(BaselineDecisionKind.ApplySnapshot, decision.Kind);
                    var state = module.Restore(message.Snapshot.StatePayload.ToByteArray(),
                        message.Snapshot.StateSchemaVersion);
                    var snapshotHash = module.ComputeStateHash(state);
                    hashes[message.Snapshot.StateVersion] = Convert.ToHexString(snapshotHash);
                    snapshotTicks.Add((message.Snapshot.Tick, snapshotHash));
                    applied = message.Snapshot.StateVersion;
                    break;
                case SyncMessage.KindOneofCase.Delta:
                    deltas++;
                    Assert.Equal(BaselineDecisionKind.ApplyDelta, decision.Kind);
                    hashes[message.Delta.StateVersion] = Convert.ToHexString(message.Delta.StateHash.ToByteArray());
                    appliedTicks.Add((message.Delta.Tick, message.Delta.StateHash.ToByteArray()));
                    Assert.Equal(applied, message.Delta.BaseVersion);
                    applied = message.Delta.StateVersion;
                    break;
                case SyncMessage.KindOneofCase.RoundResult:
                    Assert.Equal(BaselineDecisionKind.ApplyResult, decision.Kind);
                    Assert.Equal(applied, message.RoundResult.StateVersion);
                    break;
                default:
                    Assert.Fail($"Unexpected sync message kind {message.KindCase}.");
                    break;
            }
        }

        return new AppliedChain(applied, snapshots, deltas, hashes, appliedTicks, snapshotTicks, applier);
    }

    private static string WinnerOf(SyncMessage result)
    {
        using var document = System.Text.Json.JsonDocument.Parse(result.RoundResult.ResultJson);
        return document.RootElement.GetProperty("winner").GetString()!;
    }

    private static async Task<byte[]> FrameHashAsync(NpgsqlConnection connection, string roomId, long tick)
    {
        await using var command = new NpgsqlCommand(
            "SELECT state_hash FROM game.frame WHERE room_id=@room AND tick_id=@tick", connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("tick", tick);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Logs a simulator streamer in through the real StreamerServer and mounts game-a.</summary>
    private static async Task<(LoginReadyReply Login, string BindingId)> MountAsync(
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
        return (login, mounted.BindingId);
    }

    private sealed record AppliedChain(
        long FinalVersion, int Snapshots, int Deltas,
        Dictionary<long, string> VersionHashes, List<(long Tick, byte[] Hash)> AppliedTicks,
        List<(long Tick, byte[] Hash)> SnapshotTicks, BaselineApplier Applier);

    /// <summary>Renews the active owner's lease so a work-bound room never hits an expired fence.</summary>
    private sealed class RenewableClaim : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private readonly Task renewal;
        public OwnershipReply Owner { get; }

        private RenewableClaim(OwnershipReply owner, Task renewal, CancellationTokenSource cancellation)
        {
            Owner = owner;
            this.renewal = renewal;
            this.cancellation = cancellation;
        }

        public static async Task<RenewableClaim> ClaimAsync(
            ControlService.ControlServiceClient control, OwnershipRequest request)
        {
            var owner = await control.ClaimOwnershipAsync(request, deadline: DateTime.UtcNow.AddSeconds(10));
            var cancellation = new CancellationTokenSource();
            var renewal = RenewAsync(control, request, cancellation.Token);
            return new RenewableClaim(owner, renewal, cancellation);
        }

        private static async Task RenewAsync(
            ControlService.ControlServiceClient control, OwnershipRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                    await control.ClaimOwnershipAsync(request,
                        deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (RpcException) { /* the environment is disposing; renewal ends */ }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }
}
