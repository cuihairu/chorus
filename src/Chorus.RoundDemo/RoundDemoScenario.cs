using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;

namespace Chorus.RoundDemo;

/// <summary>What one streamer's edge connection observed over the whole round.</summary>
public sealed record DemoSubscriberEvidence(
    string Name, string StreamerId, long FinalTick, long FinalVersion,
    int Snapshots, int Deltas, IReadOnlyList<string> CommandReceipts, string ResultJson);

/// <summary>The demo journey's outcome, for humans to read and tests to assert.</summary>
public sealed record RoundDemoEvidence(
    string RoomId, DemoSubscriberEvidence Host, DemoSubscriberEvidence Guest,
    IReadOnlyList<(string PlayerId, long Score)> Leaderboard, string RoomStatus, long Epoch);

/// <summary>
/// The two-streamer round journey (architecture 3.2, 8.3): platform login and mount, round
/// admission, two edge subscribers streaming the committed record, one client command riding
/// the inbox with its receipt surfacing in the delta stream, the frozen result on both edges,
/// and the control plane finalizing the round and reading the leaderboard. The scenario is the
/// shared artifact: the console demo and the test suite run the identical code path against
/// real hosts — no stubs, no shortcuts.
/// </summary>
public static class RoundDemoScenario
{
    public const string RoomId = "room-demo-1";
    public const string GameId = "game-a";
    public const string BoostCommandId = "demo-boost-1";

    /// <summary>The tick at which the disconnecting edge's connection dies mid-round (AT06).</summary>
    public const int GuestDisconnectTick = 12;

    /// <param name="disconnectGuestEdge">
    /// Runs the same journey with the guest edge's connection dying at
    /// <see cref="GuestDisconnectTick"/>: the room continues without it and the reconnect
    /// carries the applied baseline back into a gapless chain (the M2 exit demonstration).
    /// </param>
    public static async Task<RoundDemoEvidence> RunAsync(
        IDemoCluster cluster, TextWriter log, bool disconnectGuestEdge = false,
        CancellationToken cancellationToken = default)
    {
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await cluster.StartAllAsync(simulatorKey, accessKey);
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);

        // Two streamers log in on the platform and mount the game (5.3): each gets a
        // binding the round routes inputs over.
        var host = await MountAsync(cluster, simulator, "demo-host", "live-demo-a", cancellationToken);
        var guest = await MountAsync(cluster, simulator, "demo-guest", "live-demo-b", cancellationToken);
        await log.WriteLineAsync($"mounted: {host.StreamerId} and {guest.StreamerId}");

        using var controlCertificate = cluster.Certificate("@cluster", "control-server", "demo-control");
        using var controlChannel = cluster.Channel(cluster.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        // The control plane owns the room lease and renews it for as long as the work is
        // alive (architecture 4.2): the same-owner renewal keeps the epoch.
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = GameId, Kind = "room", EntityId = RoomId,
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        using var renewals = new CancellationTokenSource();
        var renewalLoop = RenewAsync(control, owner, renewals.Token);

        using var gameCertificate = cluster.Certificate("tenant-a", "game-server", "game-1");
        using var gameChannel = cluster.Channel(cluster.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        var admission = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = RoomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = GameId,
            ModuleVersion = "1.0.0", ConfigHash = "cfg-demo", PackageHash = "pkg-demo",
            OperationId = "demo-start",
            Participants =
            {
                new RoundParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Role = "Host"
                },
                new RoundParticipantProto
                {
                    BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Role = "Guest"
                }
            }
        }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        await log.WriteLineAsync($"round admitted: generation {admission.RoundGeneration}");

        using var hostChannel = cluster.Channel(cluster.GamePort, controlCertificate, "game-server");
        var gameHost = new GameHost.GameHostClient(hostChannel);
        var prepared = await gameHost.PrepareRoomAsync(new PrepareRoomRequest
        {
            RoomId = RoomId, GameId = GameId, OwnerInstanceId = "game-1", Epoch = owner.Epoch,
            OperationId = "demo-run", DurationTicks = 120, RandomSeed = 6464, TickMs = 25,
            Participants =
            {
                new GameParticipantProto
                {
                    BindingId = host.BindingId, ParticipantId = host.StreamerId, Faction = "red"
                },
                new GameParticipantProto
                {
                    BindingId = guest.BindingId, ParticipantId = guest.StreamerId, Faction = "blue"
                }
            }
        }, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: cancellationToken);
        await log.WriteLineAsync($"room running: tick-0 snapshot at version {prepared.StateVersion}");

        // Platform gifts reach the round through the ingest boundary and per-binding routing.
        using var gatewayCertificate = cluster.Certificate("@cluster", "platform-gateway", "demo-gateway");
        using var gatewayChannel = cluster.Channel(cluster.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "demo-batch" };
        batch.Events.Add(new IngestEvent
        {
            // The wire constant from RedesignInputEventStore (Durable | BestEffort); the demo
            // stays a pure client and does not reference the data-server project.
            SourceDedupKey = "demo-gift-a", Durability = "Durable",
            CanonicalRoomKey = "live-demo-a", PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "demo-gift-b", Durability = "Durable",
            CanonicalRoomKey = "live-demo-b", PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch,
            deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        await new RoundInput.RoundInputClient(gameChannel).RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "demo-route", MaxEvents = 50
        }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        await log.WriteLineAsync("platform gifts routed: 100 to red, 30 to blue");

        // Both edges subscribe at the committed head baseline (8.3); the host edge also sends
        // one durable client command mid-round whose receipt must surface in the delta stream.
        // With the disconnect option the guest edge dies at the cutoff tick and rejoins later.
        var hostEdge = ReadEdgeAsync(cluster, "host edge", "demo-edge-a", host, boost: true,
            owner.Epoch, host.SessionId, null, cancellationToken);
        var guestEdge = ReadEdgeAsync(cluster, "guest edge", "demo-edge-b", guest, boost: false,
            owner.Epoch, guest.SessionId,
            disconnectGuestEdge ? GuestDisconnectTick : null, cancellationToken);
        var hostEvidence = await hostEdge;
        var guestEvidence = await guestEdge;
        await log.WriteLineAsync($"host edge: {hostEvidence.Deltas} deltas, {hostEvidence.Snapshots} snapshots, " +
            $"receipts [{string.Join(", ", hostEvidence.CommandReceipts)}], result at tick {hostEvidence.FinalTick}");
        await log.WriteLineAsync($"guest edge: {guestEvidence.Deltas} deltas, {guestEvidence.Snapshots} snapshots, " +
            $"receipts [{string.Join(", ", guestEvidence.CommandReceipts)}], result at tick {guestEvidence.FinalTick}");

        // The round's final frame carries the planned grants and the host settles the manifest
        // itself; FinalizeRound is the persistent terminator and rejects visibly until the
        // settlement completed, so the control plane simply retries (architecture 9.3).
        FinalizeRoundReply finalized = new();
        using (var settleChannel = cluster.Channel(cluster.DataPort, controlCertificate, "data-server"))
        {
            var settlement = new Settlement.SettlementClient(settleChannel);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    finalized = await settlement.FinalizeRoundAsync(new FinalizeRoundRequest
                    {
                        RoomId = RoomId, GameId = GameId, OperationId = RoomId + "/finalize",
                        TenantId = "tenant-a"
                    }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
                }
                catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.FailedPrecondition)
                {
                    finalized = new FinalizeRoundReply { Status = "Settling", Note = error.Status.Detail };
                }
                if (finalized.Status == "Settled") break;
                await Task.Delay(200, cancellationToken);
            }
            if (finalized.Status != "Settled")
                throw new InvalidOperationException($"finalization did not settle: {finalized.Note}");
            var board = await settlement.ReadLeaderboardAsync(new ReadLeaderboardRequest
            {
                GameId = GameId, BoardId = "points", MaxEntries = 10, TenantId = "tenant-a"
            }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
            var leaderboard = board.Entries.Select(entry => (entry.PlayerId, entry.Score)).ToList();
            await log.WriteLineAsync("leaderboard: " + string.Join(", ",
                leaderboard.Select(entry => $"{entry.PlayerId}={entry.Score}")));

            renewals.Cancel();
            await renewalLoop;
            return new RoundDemoEvidence(RoomId, hostEvidence, guestEvidence, leaderboard,
                finalized.Status, owner.Epoch);
        }
    }

    private static async Task<DemoSubscriberEvidence> ReadEdgeAsync(
        IDemoCluster cluster, string name, string instance, MountedStreamer streamer, bool boost,
        long epoch, string sessionId, int? disconnectAfterTick, CancellationToken cancellationToken)
    {
        using var certificate = cluster.Certificate("tenant-a", "streamer-server", instance);
        using var channel = cluster.Channel(cluster.GamePort, certificate, "game-server");
        var sync = new RoundSync.RoundSyncClient(channel);
        var commandTask = Task.CompletedTask;
        var commandSent = false;
        var snapshots = 0;
        var deltas = 0;
        var appliedVersion = 0L;
        var appliedTick = 0L;
        long? resultTick = null;
        var receipts = new List<string>();
        string result = "";

        // One message-application rule for both legs: deltas only ride the edge's own chain,
        // results only arrive on its head, and a reconnect snapshot must sit strictly above
        // the baseline the edge departed with.
        void Apply(SyncMessage message, long baselineTick, string? reconnectName = null)
        {
            switch (message.KindCase)
            {
                case SyncMessage.KindOneofCase.Snapshot:
                    snapshots++;
                    if (reconnectName is not null && message.Snapshot.Tick <= baselineTick)
                        throw new InvalidOperationException(
                            $"{reconnectName} reconnected to tick {message.Snapshot.Tick} at or " +
                            $"below its baseline {baselineTick}.");
                    appliedVersion = message.Snapshot.StateVersion;
                    appliedTick = message.Snapshot.Tick;
                    break;
                case SyncMessage.KindOneofCase.Delta:
                    deltas++;
                    if (message.Delta.BaseVersion != appliedVersion)
                        throw new InvalidOperationException(
                            $"{name} received a delta outside its chain at tick {message.Delta.Tick}.");
                    appliedVersion = message.Delta.StateVersion;
                    appliedTick = message.Delta.Tick;
                    foreach (var receipt in message.Delta.Commands)
                        receipts.Add(receipt.CommandId);
                    // One durable client command early in the stream (architecture 6.3): it
                    // enters the room inbox and its receipt returns with the frame that
                    // consumed it.
                    if (boost && !commandSent && deltas == 1)
                    {
                        commandSent = true;
                        commandTask = SendBoostAsync(cluster, sessionId, epoch, cancellationToken);
                    }
                    break;
                case SyncMessage.KindOneofCase.RoundResult:
                    if (message.RoundResult.StateVersion != appliedVersion)
                        throw new InvalidOperationException(name + " received a result off its chain.");
                    resultTick = message.RoundResult.Tick;
                    result = message.RoundResult.ResultJson;
                    break;
                default:
                    throw new InvalidOperationException($"{name} was told to resynchronize: " +
                        (message.KindCase == SyncMessage.KindOneofCase.ResyncRequired
                            ? message.ResyncRequired.Reason : message.KindCase.ToString()));
            }
        }

        using (var call = sync.Subscribe(new SubscribeRequest
        {
            RoomId = RoomId, GameId = GameId, Epoch = epoch,
            LastAppliedTick = 0, LastAppliedStateVersion = 0
        }, cancellationToken: cancellationToken))
        {
            while (await call.ResponseStream.MoveNext(cancellationToken))
            {
                Apply(call.ResponseStream.Current, 0);
                if (resultTick is not null) break;
                if (disconnectAfterTick is { } cutoff && appliedTick >= cutoff) break;
            }
        }
        // The edge's connection died at its cutoff tick: the room runs on without it (AT06),
        // and the reconnect carries the applied baseline — a baseline behind the head answers
        // with a full snapshot and the chain resumes gapless into the frozen result.
        if (disconnectAfterTick is { } && resultTick is null)
        {
            var baselineTick = appliedTick;
            await Task.Delay(400, cancellationToken);
            using var call = sync.Subscribe(new SubscribeRequest
            {
                RoomId = RoomId, GameId = GameId, Epoch = epoch,
                LastAppliedTick = baselineTick, LastAppliedStateVersion = appliedVersion
            }, cancellationToken: cancellationToken);
            while (await call.ResponseStream.MoveNext(cancellationToken))
            {
                Apply(call.ResponseStream.Current, baselineTick, "the reconnected edge");
                if (resultTick is not null) break;
            }
        }
        await commandTask;
        return new DemoSubscriberEvidence(name, streamer.StreamerId, resultTick ?? 0, appliedVersion,
            snapshots, deltas, receipts, result);
    }

    private static async Task SendBoostAsync(
        IDemoCluster cluster, string sessionId, long epoch, CancellationToken cancellationToken)
    {
        using var certificate = cluster.Certificate("tenant-a", "streamer-server", "demo-command");
        using var channel = cluster.Channel(cluster.DataPort, certificate, "data-server");
        var reply = await new RoundInput.RoundInputClient(channel).AcceptClientInputAsync(
            new ClientInputRequest
            {
                SessionId = sessionId, RoomId = RoomId, RoomEpoch = epoch, GameId = GameId,
                CommandId = BoostCommandId, PayloadJson = """{"action":"boost","level":3}"""
            }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
        if (!reply.Duplicate && reply.Sequence <= 0)
            throw new InvalidOperationException("the client command was not accepted.");
    }

    private static async Task<MountedStreamer> MountAsync(
        IDemoCluster cluster, DevelopmentPlatformSimulator simulator, string openId, string liveKey,
        CancellationToken cancellationToken)
    {
        var credential = simulator.IssueCredential("app-1", openId, liveKey, TimeSpan.FromMinutes(10));
        using var certificate = cluster.Certificate("tenant-a", "streamer-server", "streamer-" + openId);
        using var channel = cluster.Channel(cluster.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        }, cancellationToken: cancellationToken);
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = GameId,
            OperationId = "mount-" + openId
        }, cancellationToken: cancellationToken);
        return new MountedStreamer(login.StreamerId, login.SessionId, mounted.BindingId);
    }

    /// <summary>
    /// Same-owner re-claims keep the epoch and extend the lease (architecture 4.2); the loop
    /// ends with the round and swallows the disposal-time channel loss.
    /// </summary>
    private static async Task RenewAsync(
        ControlService.ControlServiceClient control, OwnershipReply owner,
        CancellationToken cancellationToken)
    {
        var request = new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = GameId, Kind = "room", EntityId = RoomId,
            OwnerInstanceId = "game-1", LeaseSeconds = 60
        };
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                var renewed = await control.ClaimOwnershipAsync(request,
                    deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
                if (renewed.Epoch != owner.Epoch)
                    throw new InvalidOperationException(
                        $"the room lease epoch moved from {owner.Epoch} to {renewed.Epoch}.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Grpc.Core.RpcException) { }
    }

    private sealed record MountedStreamer(string StreamerId, string SessionId, string BindingId);
}
