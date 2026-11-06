using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Chorus.Platform;
using Chorus.Rpc;
using Chorus.StreamerGateway.Messages;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-LINK-11's opt-in host benchmark: the full end-to-end frame channel through a real
/// cluster — the game host's RoomSubscriber (64), the gRPC RoundSync stream, the gateway's
/// EdgeRoomFanout and EdgeOutbox (256), and a real WebSocket — measured as what a client
/// actually waits between a frame being committed and it arriving. The commit clock comes
/// from the frame.commit operation receipts' committed_at (the M2-proven mapping, same host
/// so no clock skew), not from a proto field that does not exist.
///
/// It is opt-in (CHORUS_RUN_FRAME_CHANNEL=1) and reports NOT RUN otherwise, exactly like the
/// sibling capacity benchmarks — never a pass, and never a §11 claim it did not prove: it
/// reports the shape it actually ran (rooms x tickMs), not the architecture's headline.
///
/// The matchmaker hardcodes TickMs=25 (40 Hz), so the rooms are driven directly: claim →
/// start → prepare (deferred) → burst release, and each client rejoins its room by resume
/// before the burst (the FIX-LINK-08 fenced route, proven by the M4-4 parked-room flow).
/// </summary>
public sealed class RedesignFrameChannelCapacityBenchmarkTests
{
    private const string RoomPrefix = "room-frame-channel-";

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task TheHostClusterCarriesTheFrameChannelBudgetEndToEnd()
    {
        var config = FrameChannelAcceptance.LoadConfig();
        var startedUtc = DateTime.UtcNow;
        if (Environment.GetEnvironmentVariable("CHORUS_RUN_FRAME_CHANNEL") != "1")
        {
            var notRun = FrameChannelAcceptance.NotRun(config.Host,
                "set CHORUS_RUN_FRAME_CHANNEL=1 to opt in — this run does not count as a frame-channel pass",
                startedUtc, DateTime.UtcNow);
            Assert.Equal(FrameChannelVerdict.NotRun, notRun.Verdict);
            return;
        }

        var rooms = int.Parse(Environment.GetEnvironmentVariable("CHORUS_FRAME_CHANNEL_ROOMS")
            ?? config.Host.Rooms.ToString(CultureInfo.InvariantCulture));
        // The run reports the shape it actually ran; the floor scales with the room count so
        // a custom room count is still judged against the per-room frame rate it declares.
        var hostConfig = config.Host with
        {
            Rooms = rooms,
            AggregateFrameRateFloor = config.Host.AggregateFrameRateFloor * rooms / config.Host.Rooms
        };
        var measurement = await RunAsync(hostConfig);
        var report = FrameChannelAcceptance.EvaluateHost(measurement, hostConfig, startedUtc, DateTime.UtcNow);
        Assert.True(report.Verdict == FrameChannelVerdict.Passed, report.Describe());
    }

    /// <summary>Drives the host run: mount every room, prepare it deferred, seat a real WS
    /// client by resume, burst-activate the fleet, then measure commit-to-client from the
    /// frame.commit receipts against what each client actually received.</summary>
    private static async Task<FrameChannelHostMeasurement> RunAsync(FrameChannelHostConfig config)
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostRoundTests.StartEdgeClusterAsync(
            environment, config.TicksPerRoom);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;

        var clients = new List<RedesignM4WsClient>();
        var rooms = new List<DrivenRoom>();
        try
        {
            for (var index = 0; index < config.Rooms; index++)
            {
                var client = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
                clients.Add(client);
                var mount = await LoginAndStartAsync(client, simulator, $"open-frame-channel-{index}");
                var roomId = RoomPrefix + index;
                var epoch = await StartRoundAsync(environment, roomId, mount);
                await PrepareRoomAsync(environment, roomId, mount, epoch, config);
                rooms.Add(new DrivenRoom(roomId, client, mount, epoch, index));
            }

            // Seat every client before the common release: the resume re-enters the room
            // through the fenced route and the fan-out subscribes while the room is still
            // held (no frame has flowed yet), so each seat hears the whole burst.
            foreach (var room in rooms)
                await ResumeIntoRoomAsync(room);

            var snapshots = new long[config.Rooms];
            var readers = rooms.Select(room => Task.Run(() =>
                ReadDeltasAsync(room, snapshots, config))).ToList();

            var lineEnd = DateTime.UtcNow;
            using (var releaseCertificate = environment.Certificate("@cluster", "control-server",
                "frame-channel-release"))
            using (var gameChannel = environment.Channel(environment.GamePort, releaseCertificate, "game-server"))
            {
                var gameHost = new GameHost.GameHostClient(gameChannel);
                var releases = await Task.WhenAll(rooms.Select((room, index) =>
                    gameHost.ActivateRoomAsync(new ActivateRoomRequest
                    {
                        RoomId = room.RoomId, GameId = "game-a", OwnerInstanceId = "game-1",
                        Epoch = room.Epoch, OperationId = $"release-frame-channel-{index}"
                    }).ResponseAsync));
                Assert.All(releases, release => Assert.Equal("Running", release.Status));
            }

            var deadline = lineEnd + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (rooms.All(room => room.MaxTickSeen >= config.TicksPerRoom)) break;
                await Task.Delay(50, CancellationToken.None);
            }
            var ended = DateTime.UtcNow;

            foreach (var client in clients)
            {
                try { client.Dispose(); } catch { }
            }
            await Task.WhenAll(readers);

            return await MeasureAsync(config, environment, rooms, snapshots, lineEnd, ended);
        }
        finally
        {
            foreach (var client in clients)
            {
                try { client.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Measures the run: the delivery floor off the actual windows, the commit-to-client
    /// latency per received delta (warmup discarded) off the frame.commit receipts, and any
    /// re-baseline snapshot a client saw after its initial join.</summary>
    private static async Task<FrameChannelHostMeasurement> MeasureAsync(FrameChannelHostConfig config,
        RedesignM0HostTests.HostEnvironment environment, List<DrivenRoom> rooms,
        long[] snapshots, DateTime lineEnd, DateTime ended)
    {
        // The commit clock: one committed_at per (room, tick) straight off the frame.commit
        // operation receipts (M2-proven: the result_json names the tick).
        var commits = new Dictionary<(string RoomId, long Tick), DateTime>();
        await using (var connection = new NpgsqlConnection(environment.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("""
                SELECT target_id, (result_json->>'Tick')::bigint AS tick, committed_at
                FROM infra.operation_receipt
                WHERE operation_kind='frame.commit' AND tenant_id='tenant-a'
                  AND target_id LIKE @prefix
                """, connection);
            query.Parameters.AddWithValue("prefix", RoomPrefix + "%");
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                commits[(reader.GetString(0), reader.GetInt64(1))] = reader.GetDateTime(2);
        }

        long delivered = 0;
        var latencies = new List<double>();
        foreach (var room in rooms)
        {
            var warmupRemaining = config.WarmupTicks;
            foreach (var received in room.Receipts)
            {
                delivered++;
                if (warmupRemaining > 0) { warmupRemaining--; continue; }
                if (commits.TryGetValue((received.RoomId, received.Tick), out var committedAt))
                    latencies.Add((received.ReceivedUtc - committedAt).TotalMilliseconds);
            }
        }

        latencies.Sort();
        var windowSeconds = Math.Max(0.000001, (ended - lineEnd).TotalSeconds);
        long rebaselines = snapshots.Sum(count => Math.Max(0, count - 1));
        return new FrameChannelHostMeasurement(
            config.Rooms, config.TickMs, config.TicksPerRoom,
            delivered, (long)config.Rooms * config.TicksPerRoom,
            delivered / windowSeconds,
            Percentile(latencies, 0.50), Percentile(latencies, 0.95), Percentile(latencies, 0.99),
            rebaselines, null);
    }

    private static double Percentile(List<double> sorted, double quantile)
    {
        if (sorted.Count == 0) return 0;
        var index = Math.Min(sorted.Count - 1, (int)Math.Ceiling(quantile * sorted.Count) - 1);
        return sorted[Math.Max(0, index)];
    }

    /// <summary>Consumes every frame on one client's socket, recording each delta's
    /// (room, tick, receipt clock) and counting any snapshot after the initial join as a
    /// re-baseline — the single backpressure mechanism, observed at the transport.</summary>
    private static async Task ReadDeltasAsync(DrivenRoom room,
        long[] snapshots, FrameChannelHostConfig config)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var snapshotSeen = false;
        while (DateTime.UtcNow < deadline && room.MaxTickSeen < config.TicksPerRoom)
        {
            JsonElement? frame;
            try
            {
                frame = await room.Client.ReceiveAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (frame is null) return;
            var type = frame.Value.GetProperty("type").GetString();
            if (type == EdgeTypes.Snapshot)
            {
                if (snapshotSeen) Interlocked.Increment(ref snapshots[room.Index]);
                else snapshotSeen = true;
            }
            else if (type == EdgeTypes.Delta)
            {
                var tick = frame.Value.GetProperty("tickId").GetInt64();
                room.MaxTickSeen = Math.Max(room.MaxTickSeen, tick);
                lock (room.Receipts) room.Receipts.Add(new ReceivedDelta(room.RoomId, tick, DateTime.UtcNow));
            }
        }
    }

    private sealed record ReceivedDelta(string RoomId, long Tick, DateTime ReceivedUtc);

    private sealed record DrivenRoom(string RoomId, RedesignM4WsClient Client,
        Mount Mount, long Epoch, int Index)
    {
        public long MaxTickSeen;
        public List<ReceivedDelta> Receipts { get; } = [];
    }

    private sealed record Mount(string SessionId, string StreamerId, string BindingId, byte[] Secret);

    /// <summary>Logs in and starts gameplay on the client, returning the mount facts the room
    /// and the resume both name.</summary>
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

    private static async Task<long> StartRoundAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId, Mount mount)
    {
        using var controlCertificate = environment.Certificate("@cluster", "control-server",
            "frame-channel-round");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate,
            "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var owner = await control.ClaimOwnershipAsync(new OwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "room", EntityId = roomId,
            OwnerInstanceId = "game-1", LeaseSeconds = 120
        });
        Assert.True(owner.Found);
        using var dataCertificate = environment.Certificate("@cluster", "control-server",
            "frame-channel-data");
        using var dataChannel = environment.Channel(environment.DataPort, dataCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(dataChannel);
        var admission = await rounds.StartRoundAsync(new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "game-1", Epoch = owner.Epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-frame-channel", PackageHash = "pkg-1",
            OperationId = "start-" + roomId, TenantId = "tenant-a",
            Participants =
            {
                new RoundParticipantProto { BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host" }
            }
        });
        Assert.Equal("Preparing", admission.Status);
        return owner.Epoch;
    }

    private static async Task PrepareRoomAsync(RedesignM0HostTests.HostEnvironment environment,
        string roomId, Mount mount, long epoch, FrameChannelHostConfig config)
    {
        using var controlCertificate = environment.Certificate("@cluster", "control-server",
            "frame-channel-game");
        using var gameChannel = environment.Channel(environment.GamePort, controlCertificate, "game-server");
        var prepared = await new GameHost.GameHostClient(gameChannel).PrepareRoomAsync(
            new PrepareRoomRequest
            {
                RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = epoch,
                OperationId = "run-" + roomId, DurationTicks = config.TicksPerRoom, RandomSeed = 7,
                TickMs = config.TickMs, DeferredActivation = true,
                Participants =
                {
                    new GameParticipantProto { BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Faction = "red" }
                }
            });
        Assert.Equal("Prepared", prepared.Status);
    }

    /// <summary>Resumes the mounted client into its room while the room is held: the resume
    /// re-seeds the applied position and the fan-out seats the connection (roomJoined then
    /// the head snapshot) before any tick has flowed.</summary>
    private static async Task ResumeIntoRoomAsync(DrivenRoom room)
    {
        await room.Client.SendAsync(new EdgeResume
        {
            ResumeOperationId = "resume-" + room.RoomId,
            ResumeCredential = room.Mount.Secret,
            NextResumeSecret = RandomNumberGenerator.GetBytes(32),
            PreviousSessionId = room.Mount.SessionId,
            LastSeenRoomId = room.RoomId,
            LastAppliedStateVersion = 0
        });
        await room.Client.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        var rejoined = await room.Client.ExpectAsync(EdgeTypes.RoomJoined, TimeSpan.FromSeconds(30));
        Assert.Equal(room.RoomId, rejoined.GetProperty("roomId").GetString());
        await room.Client.ExpectAsync(EdgeTypes.Snapshot, TimeSpan.FromSeconds(30));
    }
}
