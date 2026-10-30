using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The M3-9 admin boundary: the AT31 trace walks a whole round — gift to delivery to frame to
/// durable effects to result to grants to ledger — and the failure commands are audited,
/// idempotent per request id, and report the current state on replay. The queue gate parks
/// pairing only; recovery keeps running and a resume cannot be undone by a replayed suspend.
/// </summary>
public sealed class RedesignM3AdminHostTests
{
    private const string Partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";

    [Fact]
    public async Task AdminTracesTheWholeRoundAndDrivesTheMissingSettlement()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });

        // A finished round whose settlement never ran — the incident the admin commands repair.
        const string room = "room-m3-9-trace";
        const string liveKey = "live-m3-9-trace";
        var mount = await MountAsync(environment, new DevelopmentPlatformSimulator(simulatorKey),
            "m3-9-trace-host", liveKey);
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-9");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        var owner = await new ControlService.ControlServiceClient(controlChannel).ClaimOwnershipAsync(
            new OwnershipRequest
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = room,
                OwnerInstanceId = "game-m3-9", LeaseSeconds = 60
            });
        using var gameCertificate = environment.Certificate("tenant-a", "game-server", "game-m3-9");
        using var gameChannel = environment.Channel(environment.DataPort, gameCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(gameChannel);
        await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = room, OwnerInstanceId = "game-m3-9", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1", PackageHash = "pkg-1", OperationId = "start-m3-9",
            Participants = { new RoundParticipantProto
            {
                BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
            } }
        });
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var activate = new NpgsqlCommand(
                $"UPDATE game.room SET status='Running' WHERE room_id='{room}'", connection);
            await activate.ExecuteNonQueryAsync();
        }
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gateway-m3-9");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var batch = new IngestBatchRequest { Platform = "simulator", AppId = "app-1", OperationId = "batch-m3-9" };
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-m3-9a", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = liveKey, PayloadJson = """{"kind":"gift","amount":100}"""
        });
        batch.Events.Add(new IngestEvent
        {
            SourceDedupKey = "gift-m3-9b", Durability = Chorus.DataServer.RedesignInputEventStore.Durable,
            CanonicalRoomKey = liveKey, PayloadJson = """{"kind":"gift","amount":30}"""
        });
        await new PlatformIngest.PlatformIngestClient(gatewayChannel).IngestBatchAsync(batch);
        var routing = new RoundInput.RoundInputClient(gameChannel);
        await routing.RouteEventsAsync(new RouteEventsRequest
        {
            Platform = "simulator", AppId = "app-1", OperationId = "route-m3-9", MaxEvents = 50
        });
        var frames = new FrameStore.FrameStoreClient(gameChannel);
        var inbox = await frames.ReadInboxAsync(new ReadInboxRequest { RoomId = room, GameId = "game-a", MaxEvents = 50 });
        Assert.Equal(2, inbox.Items.Count);
        var tick = new CommitFrameRequest
        {
            RoomId = room, GameId = "game-a", OwnerInstanceId = "game-m3-9", Epoch = owner.Epoch,
            CommitId = "tick-m3-9-1", PrevTick = 0, PrevStateVersion = 0,
            InputEventIds = { inbox.Items.Select(item => item.EventId) },
            StateHash = ByteString.CopyFrom([1, 2, 3]), ModuleVersion = "1.0.0", ConfigHash = "cfg-1"
        };
        tick.Effects.Add(new FrameEffectProto { Kind = "score", PayloadJson = """{"points":130}""" });
        var first = await frames.CommitFrameAsync(tick);
        Assert.Equal(1, first.Tick);
        var final = new CommitFrameRequest
        {
            RoomId = room, GameId = "game-a", OwnerInstanceId = "game-m3-9", Epoch = owner.Epoch,
            CommitId = "tick-m3-9-2", PrevTick = 1, PrevStateVersion = first.StateVersion,
            InputEventIds = { }, StateHash = ByteString.CopyFrom([4, 5, 6]),
            ModuleVersion = "1.0.0", ConfigHash = "cfg-1",
            Final = true, ResultJson = """{"ranking":[{"faction":"red","score":130}]}""",
            PlanHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(8))
        };
        final.Effects.Add(new FrameEffectProto { Kind = "round.finished", PayloadJson = """{"tick":2}""" });
        final.Grants.Add(new PlannedGrantProto
        {
            PlayerId = mount.StreamerId, GrantKey = "round-score", OperationId = room + "/grant/1",
            AssetId = "points", Delta = 100
        });
        final.Grants.Add(new PlannedGrantProto
        {
            PlayerId = mount.StreamerId, GrantKey = "mvp", OperationId = room + "/grant/2",
            AssetId = "points", Delta = 30
        });
        Assert.Equal(2, (await frames.CommitFrameAsync(final)).Tick);

        // The trace before the repair: the whole chain is visible, the grants sit Planned and
        // the ledger is empty — the settlement step is the missing link (AT31).
        var admin = new Admin.AdminClient(controlChannel);
        var broken = await admin.TraceRoundAsync(new AdminTraceRoundRequest { RoomId = room });
        Assert.True(broken.Found);
        Assert.Equal(2, broken.Events.Count);
        Assert.Equal(1, broken.Events[0].Sequence);
        Assert.Equal(2, broken.Events[1].Sequence);
        Assert.Contains("gift", broken.Events[0].PayloadJson);
        Assert.Equal(2, broken.Frames.Count);
        Assert.Contains(inbox.Items[0].EventId, broken.Frames[0].InputRefsJson);
        Assert.Contains("score", broken.Frames[0].EffectsJson);
        // The durable announcements include the round's own lifecycle: the start effect the
        // admission wrote plus both frame effects, all still pending a dispatcher.
        Assert.Equal(3, broken.Effects.Count);
        Assert.Equal(room + "/0/start", broken.Effects[0].EffectId);
        Assert.Equal("round.started", broken.Effects[0].Kind);
        Assert.Equal(room + "/1/0", broken.Effects[1].EffectId);
        Assert.Equal(room + "/2/0", broken.Effects[2].EffectId);
        Assert.All(broken.Effects, effect => Assert.Equal("Pending", effect.Status));
        Assert.Equal(1, broken.ResultVersion);
        Assert.Contains("ranking", broken.ResultJson);
        Assert.Equal(2, broken.Grants.Count);
        Assert.All(broken.Grants, grant => Assert.Equal("Planned", grant.Status));
        Assert.Empty(broken.Ledger);
        var snapshot = await admin.GetRoomSnapshotAsync(new AdminRoomSnapshotRequest { RoomId = room });
        Assert.True(snapshot.Found);
        Assert.Single(snapshot.Participants);
        Assert.Equal(1, snapshot.ResultVersion);
        Assert.Equal(2, snapshot.GrantsTotal);
        Assert.Equal(0, snapshot.GrantsGranted);

        // The audited repair: the same idempotent settlement primitive, driven by the control
        // plane with an operator and a reason. The duplicate replays as spent.
        var repaired = await admin.RetrySettlementAsync(new AdminSettlementRetryRequest
        {
            RequestId = "m3-9-settle-1", Operator = "alice",
            Reason = "settlement worker died before commit", RoomId = room
        });
        Assert.False(repaired.Duplicate);
        Assert.Equal(2, repaired.GrantsGranted);
        Assert.Equal(0, repaired.GrantsPending);
        var settled = await admin.TraceRoundAsync(new AdminTraceRoundRequest { RoomId = room });
        Assert.All(settled.Grants, grant => Assert.Equal("Granted", grant.Status));
        Assert.Equal(2, settled.Ledger.Count);
        Assert.Equal(settled.Grants.Select(grant => grant.OperationId).Order(),
            settled.Ledger.Select(line => line.OperationId).Order());
        Assert.Equal(130, settled.Ledger.Sum(line => line.Delta));
        var replay = await admin.RetrySettlementAsync(new AdminSettlementRetryRequest
        {
            RequestId = "m3-9-settle-1", Operator = "alice",
            Reason = "settlement worker died before commit", RoomId = room
        });
        Assert.True(replay.Duplicate);
        Assert.Equal(2, replay.GrantsGranted);
        Assert.Equal(0, replay.GrantsPending);

        // A failed command keeps its request id spent: the unknown recovery target answers
        // NotFound, and the replay is a duplicate rather than a second attempt.
        await Assert.ThrowsAsync<RpcException>(() => admin.RetryRoomRecoveryAsync(
            new AdminRoomRecoveryRequest
            {
                RequestId = "m3-9-recover-1", Operator = "alice", Reason = "move the room",
                TenantId = "tenant-a", GameId = "game-a", RoomId = room,
                ToInstanceId = "game-ghost", TickMs = 25
            }).ResponseAsync);
        var spent = await admin.RetryRoomRecoveryAsync(new AdminRoomRecoveryRequest
        {
            RequestId = "m3-9-recover-1", Operator = "alice", Reason = "move the room",
            TenantId = "tenant-a", GameId = "game-a", RoomId = room,
            ToInstanceId = "game-ghost", TickMs = 25
        });
        Assert.True(spent.Duplicate);

        // The streamer footprint: the mount's binding points at the finished room.
        var streamer = await admin.GetStreamerSnapshotAsync(
            new AdminStreamerSnapshotRequest { TenantId = "tenant-a", StreamerId = mount.StreamerId });
        Assert.True(streamer.Found);
        var binding = Assert.Single(streamer.Bindings, item => item.BindingId == mount.BindingId);
        Assert.Equal(room, binding.CurrentRoomId);
        Assert.False(streamer.TicketPresent);
        using var streamerCertificate = environment.Certificate("tenant-a", "streamer-server", "denied-m3-9");
        using var deniedChannel = environment.Channel(environment.ControlPort, streamerCertificate,
            "control-server");
        var denied = await Assert.ThrowsAsync<RpcException>(() => new Admin.AdminClient(deniedChannel)
            .GetRoomSnapshotAsync(new AdminRoomSnapshotRequest { RoomId = room }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        // A task parked in the blind dead end requeues through the audited command. The
        // NeedsAttention state itself is the store-proven territory; here the fixture parks a
        // freshly enqueued task there and the control plane overrules it once.
        using var taskChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var task = await new PlatformTask.PlatformTaskClient(taskChannel)
            .EnqueueDesiredAsync(new TaskEnqueueRequest
            {
                BindingId = mount.BindingId, OperationId = "op-m3-9-reconcile",
                DesiredJson = """{"kind":"gift-refund","order":"o-77"}"""
            });
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var park = new NpgsqlCommand(
                "UPDATE live.task SET status='NeedsAttention' WHERE task_id=@task", connection);
            park.Parameters.AddWithValue("task", task.TaskId);
            await park.ExecuteNonQueryAsync();
        }
        var requeued = await admin.ReconcilePlatformTaskAsync(new AdminTaskReconcileRequest
        {
            RequestId = "m3-9-task-1", Operator = "bob",
            Reason = "platform acknowledged the first attempt", TaskId = task.TaskId
        });
        Assert.False(requeued.Duplicate);
        Assert.Equal("Retryable", requeued.Status);
        Assert.Equal(task.TaskEpoch + 1, requeued.TaskEpoch);
        var requeuedAgain = await admin.ReconcilePlatformTaskAsync(new AdminTaskReconcileRequest
        {
            RequestId = "m3-9-task-1", Operator = "bob",
            Reason = "platform acknowledged the first attempt", TaskId = task.TaskId
        });
        Assert.True(requeuedAgain.Duplicate);

        // Every acted command is in the audit ledger with its operator and reason.
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var audit = new NpgsqlCommand("""
                SELECT operator,command FROM control.audit
                WHERE request_id IN ('m3-9-settle-1','m3-9-recover-1','m3-9-task-1')
                ORDER BY audit_id
                """, connection);
            await using var reader = await audit.ExecuteReaderAsync();
            var rows = new List<(string Operator, string Command)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1)));
            Assert.Equal([
                ("alice", "RetrySettlement"),
                ("alice", "RetryRoomRecovery"),
                ("bob", "ReconcilePlatformTask")
            ], rows);
        }
    }

    [Fact]
    public async Task SuspendedQueueParksPairingUntilAResume()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_GAME_ENDPOINTS"] = $"https://localhost:{environment.GamePort}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort, "streamer-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });
        await environment.StartAsync("Chorus.GameServer", environment.GamePort, "game-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture)
            },
            clientTenant: "tenant-a");

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var host = await MountAsync(environment, simulator, "m3-9-gate-host", "live-m3-9-gate-a");
        var guest = await MountAsync(environment, simulator, "m3-9-gate-guest", "live-m3-9-gate-b");
        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-9");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        var admin = new Admin.AdminClient(controlChannel);

        // The gate lands before the tickets: pairing is parked from the first cycle on.
        var suspended = await admin.SuspendMatchQueueAsync(new AdminQueueGateRequest
        {
            RequestId = "m3-9-gate-1", Operator = "carol",
            Reason = "incident 4123: gift spam floods the queue", PartitionKey = Partition
        });
        Assert.True(suspended.Suspended);
        Assert.False(suspended.Duplicate);
        await EnqueueAsync(environment, host, "m3-9-enqueue-a");
        await EnqueueAsync(environment, guest, "m3-9-enqueue-b");

        // Several coordinator cycles pass; two compatible candidates sit in the same window and
        // neither moves — parked, not lost.
        await Task.Delay(2500);
        using var dataChannel = environment.Channel(environment.DataPort, controlCertificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(dataChannel);
        var hostTicket = await queue.GetTicketAsync(
            new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId });
        var guestTicket = await queue.GetTicketAsync(
            new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId });
        Assert.Equal("Queued", hostTicket.Ticket.State);
        Assert.Equal("Queued", guestTicket.Ticket.State);

        // A replayed suspend is a duplicate reporting the gate as it stands.
        var suspendedAgain = await admin.SuspendMatchQueueAsync(new AdminQueueGateRequest
        {
            RequestId = "m3-9-gate-1", Operator = "carol",
            Reason = "incident 4123: gift spam floods the queue", PartitionKey = Partition
        });
        Assert.True(suspendedAgain.Duplicate);
        Assert.True(suspendedAgain.Suspended);

        // The resume opens the gate; the pair commits. The old suspend replayed after the resume
        // reports the current state — it cannot re-park what a later command reopened.
        var resumed = await admin.ResumeMatchQueueAsync(new AdminQueueGateRequest
        {
            RequestId = "m3-9-gate-2", Operator = "carol",
            Reason = "incident 4123 drained", PartitionKey = Partition
        });
        Assert.False(resumed.Suspended);
        Assert.False(resumed.Duplicate);
        var staleSuspend = await admin.SuspendMatchQueueAsync(new AdminQueueGateRequest
        {
            RequestId = "m3-9-gate-1", Operator = "carol",
            Reason = "incident 4123: gift spam floods the queue", PartitionKey = Partition
        });
        Assert.True(staleSuspend.Duplicate);
        Assert.False(staleSuspend.Suspended);
        var paired = await WaitForAsync(async () =>
        {
            var left = await queue.GetTicketAsync(
                new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = host.BindingId });
            var right = await queue.GetTicketAsync(
                new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = guest.BindingId });
            return left.Ticket.State == "Committed" && right.Ticket.State == "Committed"
                && left.Ticket.ReservationId == right.Ticket.ReservationId;
        }, TimeSpan.FromSeconds(60));
        Assert.True(paired, "The resumed queue never paired the parked candidates.");

        // The drain command marks a registered process; a duplicate reports it, an unknown one
        // answers. This cluster runs without an agent, so the process table row is the fixture —
        // the drain semantics are the row's visible lifecycle, not how it registered.
        const string instance = "game-m3-9-drain";
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.node(node_id,agent_instance_id) VALUES ('node-m3-9','agent-m3-9');
                INSERT INTO control.deployment(slot_id,node_id,role,desired_version,plan_generation,
                    desired_state,package_hash)
                VALUES ('game@node-m3-9','node-m3-9','game-server','1.0.0',1,'Running','pkg-1');
                INSERT INTO control.process(process_instance_id,slot_id,node_id,agent_instance_id,
                    report_sequence,lifecycle,readiness,health,admission,started_at)
                VALUES ('game-m3-9-drain','game@node-m3-9','node-m3-9','agent-m3-9',1,
                    'Running','Ready','Healthy','Accepting',clock_timestamp());
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }
        var drained = await admin.DrainProcessAsync(new AdminDrainProcessRequest
        {
            RequestId = "m3-9-drain-1", Operator = "carol",
            Reason = "rolling restart after the incident", ProcessInstanceId = instance
        });
        Assert.True(drained.Found);
        Assert.Equal("Draining", drained.Lifecycle);
        var drainedAgain = await admin.DrainProcessAsync(new AdminDrainProcessRequest
        {
            RequestId = "m3-9-drain-1", Operator = "carol",
            Reason = "rolling restart after the incident", ProcessInstanceId = instance
        });
        Assert.True(drainedAgain.Duplicate);
        Assert.Equal("Draining", drainedAgain.Lifecycle);
        var unknown = await Assert.ThrowsAsync<RpcException>(() => admin.DrainProcessAsync(
            new AdminDrainProcessRequest
            {
                RequestId = "m3-9-drain-2", Operator = "carol",
                Reason = "ghost process", ProcessInstanceId = "process-ghost"
            }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);

        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var audit = new NpgsqlCommand("""
                SELECT operator,command FROM control.audit
                WHERE request_id LIKE 'm3-9-gate-%' OR request_id LIKE 'm3-9-drain-%'
                ORDER BY audit_id
                """, connection);
            await using var reader = await audit.ExecuteReaderAsync();
            var rows = new List<(string Operator, string Command)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1)));
            Assert.Equal([
                ("carol", "SuspendMatchQueue"),
                ("carol", "ResumeMatchQueue"),
                ("carol", "DrainProcess"),
                ("carol", "DrainProcess") // the NotFound attempt kept its audit row: spent
            ], rows);
            await reader.DisposeAsync();
            await using var gate = new NpgsqlCommand(
                "SELECT suspended FROM control.queue_gate WHERE partition_key=@partition", connection);
            gate.Parameters.AddWithValue("partition", Partition);
            Assert.False(await gate.ExecuteScalarAsync() is true);
        }
    }

    private static async Task EnqueueAsync(RedesignM0HostTests.HostEnvironment environment,
        (string StreamerId, string SessionId, string BindingId) mount, string operationId)
    {
        using var certificate = environment.Certificate("tenant-a", "streamer-server",
            "streamer-" + operationId);
        using var channel = environment.Channel(environment.DataPort, certificate, "data-server");
        var queue = new MatchQueue.MatchQueueClient(channel);
        await queue.EnqueueTicketAsync(new EnqueueTicketRequest
        {
            TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId,
            SessionId = mount.SessionId, PartitionKey = Partition, OperationId = operationId
        });
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
}
