using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using RoutingService = Chorus.Rpc.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// The dynamic game-host directory (FIX-SCHED-01) on real hosts: a game instance that starts
/// after the control plane is discovered from its self-reports and paired with — no restart,
/// no configuration — a restarted instance is reached at its new address, and a silent
/// instance ages out so its stale address never receives a new assignment.
/// </summary>
public sealed class RedesignClusterDirectoryHostTests
{
    private const string Partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";

    [Fact]
    public async Task ANewGameHostIsDiscoveredAndPairedWithoutAControlRestart()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var controlEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
            // No configured game endpoints: the directory starts empty and must be filled by
            // the self-report → refresh loop, not by configuration or a restart.
            ["CHORUS_MATCH_INTERVAL_MS"] = "200",
            ["CHORUS_DIRECTORY_REFRESH_MS"] = "500",
            ["CHORUS_DIRECTORY_STALE_SECONDS"] = "15",
            // Long rounds: the assertions read live rooms from the routing snapshot, so the
            // pairs must not settle and retire before the test has looked at them.
            ["CHORUS_ROUND_DURATION_TICKS"] = "10000"
        };
        using var fleet = await FleetAsync(environment, simulatorKey, accessKey, controlEnvironment);
        var gamePort = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.GameServer", gamePort, "game-server",
            GameEnvironment(environment.DataPort, fleet.ControlEndpoint, null),
            clientTenant: "tenant-a", instanceId: "game-sched-a");

        // The self-report lands in the control plane's process table; the routing snapshot
        // is the visible proof of the report the directory refresh will fold in.
        var registered = await WaitForAsync(async () =>
            (await fleet.Routing.GetRoutingSnapshotAsync(new Empty())).Instances.Any(
                instance => instance.ProcessInstanceId == "game-sched-a"), TimeSpan.FromSeconds(30));
        Assert.True(registered, "The self-reporting host never appeared in the routing snapshot.");

        var mounts = new List<(string StreamerId, string SessionId, string BindingId)>();
        for (var index = 0; index < 2; index++)
            mounts.Add(await MountAsync(environment, fleet.Simulator, $"sched-new-{index}"));
        foreach (var (mount, index) in mounts.Select((mount, index) => (mount, index)))
            await EnqueueAsync(environment, mount, $"sched-new-enqueue-{index}");

        var committed = await WaitForAsync(async () =>
        {
            foreach (var mount in mounts)
            {
                var ticket = await fleet.Queue.GetTicketAsync(
                    new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId });
                if (ticket.Ticket.State != "Committed") return false;
            }
            return true;
        }, TimeSpan.FromSeconds(60));
        Assert.True(committed, "The coordinator never paired on the discovered host.");

        var snapshot = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        var room = Assert.Single(snapshot.Rooms);
        Assert.Equal("game-sched-a", room.OwnerInstanceId);
        // The owner resolves to the address the host reported, not to any configured seed —
        // there was none.
        Assert.Equal($"https://localhost:{gamePort}", room.Endpoint);
    }

    [Fact]
    public async Task ARestartedGameHostServesItsNewAddressWithoutAControlRestart()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var portOne = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
            // The seed is bootstrap only: the restarted incarnation's report must take the
            // entry over and replace the address, or the coordinator keeps dialing the grave.
            ["CHORUS_GAME_ENDPOINTS"] = $"game-sched-b=https://localhost:{portOne}",
            ["CHORUS_MATCH_INTERVAL_MS"] = "200",
            ["CHORUS_DIRECTORY_REFRESH_MS"] = "500",
            ["CHORUS_DIRECTORY_STALE_SECONDS"] = "30",
            ["CHORUS_ROUND_DURATION_TICKS"] = "10000"
        };
        using var fleet = await FleetAsync(environment, simulatorKey, accessKey, controlEnvironment);
        await environment.StartAsync("Chorus.GameServer", portOne, "game-server",
            GameEnvironment(environment.DataPort, fleet.ControlEndpoint, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-sched-b");

        await PairAndCommitAsync(environment, fleet, "sched-r1", 2);
        var beforeRestart = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        var firstRoom = Assert.Single(beforeRestart.Rooms);
        Assert.Equal("game-sched-b", firstRoom.OwnerInstanceId);
        Assert.Equal($"https://localhost:{portOne}", firstRoom.Endpoint);

        // A crash and a restart under the same instance id on a new port: the report cycle
        // hands the row to the newer incarnation, the directory re-points the entry.
        environment.Kill("game-sched-b");
        var portTwo = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.GameServer", portTwo, "game-server",
            GameEnvironment(environment.DataPort, fleet.ControlEndpoint, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-sched-b");
        var converged = await WaitForAsync(async () =>
            (await fleet.Routing.GetRoutingSnapshotAsync(new Empty())).Instances.Any(
                instance => instance.ProcessInstanceId == "game-sched-b"
                    && instance.Endpoint == $"https://localhost:{portTwo}"), TimeSpan.FromSeconds(30));
        Assert.True(converged, "The restarted incarnation never took its process row over.");

        var waveTwo = await PairAndCommitAsync(environment, fleet, "sched-r2", 2);
        // The wave-2 mounts name their own rooms: whatever the dead room's tickets did in the
        // background (settle, re-pair, split this wave across two rooms), every room THIS wave
        // landed in must sit at the restarted incarnation's address.
        var waveTwoRooms = await RoomsOfAsync(fleet, waveTwo);
        Assert.NotEmpty(waveTwoRooms);
        Assert.DoesNotContain(firstRoom.RoomId, waveTwoRooms);
        var snapshot = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        foreach (var roomId in waveTwoRooms)
        {
            var room = Assert.Single(snapshot.Rooms, candidate => candidate.RoomId == roomId);
            Assert.Equal("game-sched-b", room.OwnerInstanceId);
            Assert.Equal($"https://localhost:{portTwo}", room.Endpoint);
        }
    }

    [Fact]
    public async Task ASilentGameHostAgesOutOfNewAssignments()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var controlEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
            // A short staleness gate so the eviction is observable inside the test window.
            ["CHORUS_MATCH_INTERVAL_MS"] = "200",
            ["CHORUS_DIRECTORY_REFRESH_MS"] = "500",
            ["CHORUS_DIRECTORY_STALE_SECONDS"] = "6",
            ["CHORUS_ROUND_DURATION_TICKS"] = "10000"
        };
        using var fleet = await FleetAsync(environment, simulatorKey, accessKey, controlEnvironment);
        var portA = RedesignM0HostTests.HostEnvironment.FreePort();
        var portB = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.GameServer", portA, "game-server",
            GameEnvironment(environment.DataPort, fleet.ControlEndpoint, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-sched-a");
        await environment.StartAsync("Chorus.GameServer", portB, "game-server",
            GameEnvironment(environment.DataPort, fleet.ControlEndpoint, "node-a/game-server#2"),
            clientTenant: "tenant-a", instanceId: "game-sched-b");
        var bothReported = await WaitForAsync(async () =>
            (await fleet.Routing.GetRoutingSnapshotAsync(new Empty())).Instances.Count == 2,
            TimeSpan.FromSeconds(30));
        Assert.True(bothReported, "The two self-reporting hosts never appeared.");

        // Wave one proves both hosts are assignable: round-robin spreads the two pairs.
        await PairAndCommitAsync(environment, fleet, "sched-s1", 4);
        var before = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        Assert.Equal(2, before.Rooms.Count);
        Assert.Equal(2, before.Rooms.Select(room => room.OwnerInstanceId).Distinct().Count());

        environment.Kill("game-sched-b");
        // Past the staleness gate plus a refresh margin, the silent host has left the
        // directory; whatever pairs next must land on the live host alone.
        await Task.Delay(TimeSpan.FromSeconds(9));
        var waveTwo = await PairAndCommitAsync(environment, fleet, "sched-s2", 2);
        var waveTwoRooms = await RoomsOfAsync(fleet, waveTwo);
        Assert.NotEmpty(waveTwoRooms);
        var after = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        foreach (var roomId in waveTwoRooms)
        {
            var room = Assert.Single(after.Rooms, candidate => candidate.RoomId == roomId);
            Assert.Equal("game-sched-a", room.OwnerInstanceId);
            Assert.Equal($"https://localhost:{portA}", room.Endpoint);
        }
    }

    [Fact]
    public async Task FullAndDrainedHostsTakeNoNewRoomsWhileKeepingTheirOwn()
    {
        // FIX-SCHED-02's acceptance: a host full at its own budget and a host the admin
        // drained take no new rooms — the queue waits instead — while the rooms they already
        // hold keep living where they are. game-sched-b is capped at one room (FIX-PERF-07);
        // when it holds its cap its own report says Full, and the scheduler stops offering.
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        var controlEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
            ["CHORUS_MATCH_INTERVAL_MS"] = "200",
            ["CHORUS_DIRECTORY_REFRESH_MS"] = "500",
            ["CHORUS_DIRECTORY_STALE_SECONDS"] = "6",
            ["CHORUS_ROUND_DURATION_TICKS"] = "10000"
        };
        using var fleet = await FleetAsync(environment, simulatorKey, accessKey, controlEnvironment);
        var portA = RedesignM0HostTests.HostEnvironment.FreePort();
        var portB = RedesignM0HostTests.HostEnvironment.FreePort();
        await environment.StartAsync("Chorus.GameServer", portA, "game-server",
            GameEnvironment(environment.DataPort, fleet.ControlEndpoint, "node-a/game-server"),
            clientTenant: "tenant-a", instanceId: "game-sched-a");
        var capped = GameEnvironment(environment.DataPort, fleet.ControlEndpoint, "node-a/game-server#2");
        capped["CHORUS_GAME_MAX_ACTIVE_ROOMS"] = "1";
        await environment.StartAsync("Chorus.GameServer", portB, "game-server", capped,
            clientTenant: "tenant-a", instanceId: "game-sched-b");
        var bothReported = await WaitForAsync(async () =>
            (await fleet.Routing.GetRoutingSnapshotAsync(new Empty())).Instances.Count == 2,
            TimeSpan.FromSeconds(30));
        Assert.True(bothReported, "The two self-reporting hosts never appeared.");
        // One refresh beat so the directory folds both before the first pairing cycle.
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        // Wave one: both hosts admit; the round-robin spreads one room to each.
        await PairAndCommitAsync(environment, fleet, "sched-full", 4);
        var afterWaveOne = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        Assert.Equal(2, afterWaveOne.Rooms.Count);
        Assert.Equal(2, afterWaveOne.Rooms.Select(room => room.OwnerInstanceId).Distinct().Count());
        var roomOnCapped = afterWaveOne.Rooms
            .Single(room => room.OwnerInstanceId == "game-sched-b").RoomId;
        var roomOnDrainTarget = afterWaveOne.Rooms
            .Single(room => room.OwnerInstanceId == "game-sched-a").RoomId;

        // The capped host's own report announces Full: budget → self-report → snapshot.
        var fullAnnounced = await WaitForAsync(async () =>
            (await fleet.Routing.GetRoutingSnapshotAsync(new Empty())).Instances.Any(instance =>
                instance.ProcessInstanceId == "game-sched-b" && instance.Admission == "Full"),
            TimeSpan.FromSeconds(15));
        Assert.True(fullAnnounced, "The capped host never announced Full.");
        // The announce proves the STORE flipped; the directory folds on its own refresh beat
        // (500ms), so give the fold a beat before asking the scheduler to respect the word.
        await Task.Delay(TimeSpan.FromMilliseconds(1000));

        // Wave two pairs while the capped host is Full: only the uncapped host is eligible.
        var waveTwo = await PairAndCommitAsync(environment, fleet, "sched-next", 2);
        var waveTwoRooms = await RoomsOfAsync(fleet, waveTwo);
        Assert.NotEmpty(waveTwoRooms);
        var afterWaveTwo = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        foreach (var roomId in waveTwoRooms)
        {
            var room = Assert.Single(afterWaveTwo.Rooms, candidate => candidate.RoomId == roomId);
            Assert.Equal("game-sched-a", room.OwnerInstanceId);
            Assert.Equal($"https://localhost:{portA}", room.Endpoint);
        }

        // The admin drains the other host — it holds rooms that must keep living.
        using var adminCertificate = environment.Certificate("@cluster", "control-server", "admin-sched");
        using var adminChannel = environment.Channel(environment.ControlPort, adminCertificate,
            "control-server");
        var drained = await new Admin.AdminClient(adminChannel).DrainProcessAsync(
            new AdminDrainProcessRequest
            {
                RequestId = "drain-sched-a", Operator = "acceptance", Reason = "FIX-SCHED-02",
                ProcessInstanceId = "game-sched-a"
            });
        Assert.Equal("Draining", drained.Lifecycle);
        var drainAnnounced = await WaitForAsync(async () =>
            (await fleet.Routing.GetRoutingSnapshotAsync(new Empty())).Instances.Any(instance =>
                instance.ProcessInstanceId == "game-sched-a" && instance.Admission == "Draining"),
            TimeSpan.FromSeconds(15));
        Assert.True(drainAnnounced, "The drained host's admission never settled to Draining.");
        // Same two-plane beat as above: without it a fast mount can enqueue before the
        // directory folds the drain, and the coordinator would rightly pair onto the stale
        // word — the race is in the test's pace, not in the scheduler.
        await Task.Delay(TimeSpan.FromMilliseconds(1000));

        // Admission stops, service does not: the drained host's room stays routed to it, and
        // the capped host's room keeps its owner too.
        var afterDrain = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        Assert.Equal("game-sched-a", Assert.Single(afterDrain.Rooms,
            candidate => candidate.RoomId == roomOnDrainTarget).OwnerInstanceId);
        Assert.Equal("game-sched-b", Assert.Single(afterDrain.Rooms,
            candidate => candidate.RoomId == roomOnCapped).OwnerInstanceId);

        // No eligible host remains — one Draining, one Full — so wave three's tickets park
        // in the queue instead of being forced onto an ineligible host.
        var waveThree = new List<(string StreamerId, string SessionId, string BindingId)>();
        for (var index = 0; index < 2; index++)
        {
            var mount = await MountAsync(environment, fleet.Simulator, $"sched-park-{index}");
            waveThree.Add(mount);
            await EnqueueAsync(environment, mount, $"sched-park-enqueue-{index}");
        }
        var parked = true;
        var parkDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < parkDeadline)
        {
            foreach (var mount in waveThree)
            {
                var ticket = await fleet.Queue.GetTicketAsync(new GetTicketRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId
                });
                if (ticket.Ticket.State == "Committed") parked = false;
            }
            if (!parked) break;
            await Task.Delay(300);
        }
        Assert.True(parked, "A room was forced onto a full or drained host. " + await ParkDiagnosticsAsync(fleet, waveThree));
        var afterPark = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        Assert.True(afterWaveTwo.Rooms.Count == afterPark.Rooms.Count,
            "The room set changed while no host was eligible. " + await ParkDiagnosticsAsync(fleet, waveThree)
            + $" before={afterWaveTwo.Rooms.Count} after={afterPark.Rooms.Count}");
    }

    /// <summary>The park-window diagnostics: every instance's readiness/admission and every
    /// wave-three room's owner, so a forced pairing says who took it and under what word.</summary>
    private static async Task<string> ParkDiagnosticsAsync(Fleet fleet,
        IReadOnlyList<(string StreamerId, string SessionId, string BindingId)> mounts)
    {
        var snapshot = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        var instances = string.Join(",", snapshot.Instances.Select(instance =>
            $"{instance.ProcessInstanceId}:{instance.Readiness}/{instance.Admission}"));
        var rooms = string.Join(",", snapshot.Rooms.Select(room =>
            $"{room.RoomId}@{room.OwnerInstanceId}:{room.Status}"));
        var bound = string.Join(",", snapshot.Streamers
            .Where(row => mounts.Any(mount => mount.StreamerId == row.StreamerId))
            .Select(row => $"{row.StreamerId}->{row.RoomId}"));
        return $"instances[{instances}] rooms[{rooms}] parked[{bound}]";
    }

    private sealed class Fleet : IDisposable
    {
        public DevelopmentPlatformSimulator Simulator { get; }
        public string ControlEndpoint { get; }
        public RoutingService.RoutingClient Routing { get; }
        public MatchQueue.MatchQueueClient Queue { get; }
        private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate;
        private readonly Grpc.Net.Client.GrpcChannel _routingChannel;
        private readonly Grpc.Net.Client.GrpcChannel _dataChannel;

        public Fleet(RedesignM0HostTests.HostEnvironment environment,
            DevelopmentPlatformSimulator simulator, string controlEndpoint)
        {
            Simulator = simulator;
            ControlEndpoint = controlEndpoint;
            _certificate = environment.Certificate("@cluster", "control-server", "control-sched");
            _routingChannel = environment.Channel(environment.ControlPort, _certificate, "control-server");
            _dataChannel = environment.Channel(environment.DataPort, _certificate, "data-server");
            Routing = new RoutingService.RoutingClient(_routingChannel);
            Queue = new MatchQueue.MatchQueueClient(_dataChannel);
        }

        public void Dispose()
        {
            _routingChannel.Dispose();
            _dataChannel.Dispose();
            _certificate.Dispose();
        }
    }

    private static async Task<Fleet> FleetAsync(RedesignM0HostTests.HostEnvironment environment,
        byte[] simulatorKey, byte[] accessKey, IReadOnlyDictionary<string, string> controlEnvironment)
    {
        var streamerEnvironment = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", controlEnvironment);
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", streamerEnvironment);
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-sched"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-sched" });
        }
        return new Fleet(environment, new DevelopmentPlatformSimulator(simulatorKey),
            $"https://localhost:{environment.ControlPort}");
    }

    /// <summary>Mounts count streamers, enqueues them and waits until every ticket committed.</summary>
    private static async Task<List<(string StreamerId, string SessionId, string BindingId)>> PairAndCommitAsync(
        RedesignM0HostTests.HostEnvironment environment, Fleet fleet, string tag, int count)
    {
        var mounts = new List<(string StreamerId, string SessionId, string BindingId)>();
        for (var index = 0; index < count; index++)
            mounts.Add(await MountAsync(environment, fleet.Simulator, $"{tag}-{index}"));
        foreach (var (mount, index) in mounts.Select((mount, index) => (mount, index)))
            await EnqueueAsync(environment, mount, $"{tag}-enqueue-{index}");
        var committed = await WaitForAsync(async () =>
        {
            foreach (var mount in mounts)
            {
                var ticket = await fleet.Queue.GetTicketAsync(
                    new GetTicketRequest { TenantId = "tenant-a", GameId = "game-a", BindingId = mount.BindingId });
                if (ticket.Ticket.State != "Committed") return false;
            }
            return true;
        }, TimeSpan.FromSeconds(60));
        Assert.True(committed, $"The coordinator did not commit the pairs for {tag}.");
        return mounts;
    }

    /// <summary>The rooms a wave's mounts actually landed in: read from the streamer routes,
    /// not by diffing snapshots — a killed host's wave can be re-paired behind the test's back
    /// and a diff would count that room as fresh.</summary>
    private static async Task<List<string>> RoomsOfAsync(Fleet fleet,
        IReadOnlyList<(string StreamerId, string SessionId, string BindingId)> mounts)
    {
        var snapshot = await fleet.Routing.GetRoutingSnapshotAsync(new Empty());
        return mounts
            .Select(mount => snapshot.Streamers.Single(row => row.StreamerId == mount.StreamerId).RoomId)
            .Where(roomId => roomId.Length > 0)
            .Distinct().ToList();
    }

    private static Dictionary<string, string> GameEnvironment(int dataPort, string controlEndpoint,
        string? slotId)
    {
        var variables = new Dictionary<string, string>
        {
            ["CHORUS_DATA_PORT"] = dataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CHORUS_CONTROL_ENDPOINT"] = controlEndpoint,
            ["CHORUS_REPORT_INTERVAL_MS"] = "500"
        };
        if (slotId is not null) variables["CHORUS_SLOT_ID"] = slotId;
        return variables;
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
        RedesignM0HostTests.HostEnvironment environment, DevelopmentPlatformSimulator simulator, string openId)
    {
        var liveKey = "live-sched-" + openId;
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
