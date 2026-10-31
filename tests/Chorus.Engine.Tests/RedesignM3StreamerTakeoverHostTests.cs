using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using RoutingService = Chorus.Rpc.Routing;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The M3-7b streamer-entity takeover (architecture 10.3, single-host form): the instance that
/// grants a session puts the entity on the control-plane ownership record; when that instance
/// goes silent, the control plane hands the entity to a second instance, the streamer resumes
/// through it — recovering the actor and confirming the ownership in one step — and session
/// rotation fences everything the displaced instance might still say. The SIGSTOP freeze is a
/// process-level partition approximation; the cross-host form stays open in the ledger.
/// </summary>
public sealed class RedesignM3StreamerTakeoverHostTests
{
    private const int SigStop = 19;
    private const int SigCont = 18;

    [Fact]
    public async Task ASilentStreamerHolderIsTakenOverAndTheOldSessionIsFencedByRotation()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var streamerPortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var streamerPortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        // The control plane runs the ownership registrar short, so a silent holder's lease dies
        // within seconds and the handover is observable in test time.
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "6",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "3"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        // Two streamer hosts with one platform configuration: A holds the entity, B takes over.
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        StreamerEnvironment StreamerEnv(int port, string slotId) => new(
            environment.DataPort, controlEndpoint, port, slotId);
        await environment.StartAsync("Chorus.StreamerServer", streamerPortA, "streamer-server",
            StreamerEnv(streamerPortA, "node-a/streamer-server").Environment(simulatorKey, accessKey),
            instanceId: "streamer-a");
        await environment.StartAsync("Chorus.StreamerServer", streamerPortB, "streamer-server",
            StreamerEnv(streamerPortB, "node-a/streamer-server#2").Environment(simulatorKey, accessKey),
            instanceId: "streamer-b");
        var hostA = environment.HostProcesses.Single(entry => entry.Instance == "streamer-a").Process;
        Assert.False(hostA.HasExited);

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-m3-7b");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);
        var takeover = new Takeover.TakeoverClient(controlChannel);

        // The node exists because an agent registered it; the streamer hosts then report
        // themselves into that node (architecture 4.4).
        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-m3-7b"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-m3-7b" });
        }

        // The streamer logs in through A: the session grant lands there, and A puts the entity
        // on the control-plane ownership record.
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var resumeSecret = RandomNumberGenerator.GetBytes(32);
        (string StreamerId, string SessionId, string AccessCredential, string BindingId) mounted;
        using (var certificate = environment.Certificate("tenant-a", "streamer-server", "login-at-s7b"))
        using (var channel = environment.Channel(streamerPortA, certificate, "streamer-server"))
        {
            var session = new StreamerSession.StreamerSessionClient(channel);
            var login = await session.LoginAsync(new LoginRequest
            {
                Platform = "simulator", AppId = "app-1",
                LaunchCredential = simulator.IssueCredential("app-1", "open-at-s7b", "live-at-s7b",
                    TimeSpan.FromMinutes(10)),
                LoginAttemptId = "attempt-at-s7b",
                NextResumeSecret = ByteString.CopyFrom(resumeSecret),
                GatewayInstanceId = "gateway-1", ConnectionId = "connection-a"
            });
            var binding = await session.StartGameplayAsync(new StartGameplayRequest
            {
                AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-at-s7b"
            });
            mounted = (login.StreamerId, login.SessionId, login.AccessCredential, binding.BindingId);
        }

        var claimed = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = mounted.StreamerId
            });
            return ownership.Found && ownership.OwnerInstanceId == "streamer-a" && ownership.Status == "Active";
        }, TimeSpan.FromSeconds(15));
        Assert.True(claimed, "The login host never claimed the streamer entity ownership.");
        var epoch = (await control.GetOwnershipAsync(new OwnershipLookup
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = mounted.StreamerId
        })).Epoch;

        // The routing snapshot names the holder and its epoch alongside the mounted binding.
        var before = await routing.GetRoutingSnapshotAsync(new Empty());
        var routeBefore = Assert.Single(before.Streamers, entry => entry.StreamerId == mounted.StreamerId);
        Assert.Equal("streamer-a", routeBefore.OwnerInstanceId);
        Assert.Equal(epoch, routeBefore.Epoch);

        // A goes silent: SIGSTOP keeps the process alive but deaf. Its self-reports stop, the
        // registrar gate closes and the lease runs out — the entity becomes takeable.
        _ = kill(hostA.Id, SigStop);
        var leaseExpired = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = mounted.StreamerId
            });
            return ownership.Found
                && ownership.LeaseUntilUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }, TimeSpan.FromSeconds(40));
        Assert.True(leaseExpired, "The frozen holder's lease never expired.");

        // The control plane hands the entity to B: the epoch bumps and the row parks Recovering.
        var transferred = await takeover.TransferOwnershipAsync(new TransferOwnershipRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = mounted.StreamerId,
            ToInstanceId = "streamer-b", LeaseSeconds = 15
        });
        Assert.Equal("streamer-b", transferred.OwnerInstanceId);
        Assert.Equal("Recovering", transferred.Status);
        Assert.Equal(epoch + 1, transferred.Epoch);

        // The streamer resumes through B: rotation grants a fresh session there, the actor is
        // recovered from the store (the mounted binding included), and B's claim confirms the
        // ownership — same holder with a live lease keeps the epoch, status back to Active.
        var nextSecret = RandomNumberGenerator.GetBytes(32);
        string accessAfterResume;
        using (var certificate = environment.Certificate("tenant-a", "streamer-server", "resume-at-s7b"))
        using (var channel = environment.Channel(streamerPortB, certificate, "streamer-server"))
        {
            var session = new StreamerSession.StreamerSessionClient(channel);
            var resumed = await session.ResumeAsync(new ResumeRequest
            {
                PreviousSessionId = mounted.SessionId, ResumeOperationId = "resume-at-s7b",
                PreviousSecret = ByteString.CopyFrom(resumeSecret),
                NextSecret = ByteString.CopyFrom(nextSecret),
                GatewayInstanceId = "gateway-2", ConnectionId = "connection-b"
            });
            accessAfterResume = resumed.AccessCredential;
            // The recovered actor still carries the mount: the binding A granted is the binding
            // B restores — the entity did not lose its gameplay by changing instances.
            var status = await session.GameplayStatusAsync(new GameplayStatusRequest
            {
                AccessCredential = accessAfterResume
            });
            Assert.Equal(mounted.BindingId, status.BindingId);
            Assert.Equal("game-a", status.GameId);
        }

        var ownershipAfter = await control.GetOwnershipAsync(new OwnershipLookup
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = mounted.StreamerId
        });
        Assert.True(ownershipAfter.Found);
        Assert.Equal("streamer-b", ownershipAfter.OwnerInstanceId);
        Assert.Equal(epoch + 1, ownershipAfter.Epoch);
        Assert.Equal("Active", ownershipAfter.Status);

        // The routing snapshot follows the handover at the bumped epoch.
        var after = await routing.GetRoutingSnapshotAsync(new Empty());
        var routeAfter = Assert.Single(after.Streamers, entry => entry.StreamerId == mounted.StreamerId);
        Assert.Equal("streamer-b", routeAfter.OwnerInstanceId);
        Assert.Equal(epoch + 1, routeAfter.Epoch);

        // Session rotation fences the displaced instance: A wakes, and the credential it issued
        // no longer names the current session — even the read path refuses it.
        _ = kill(hostA.Id, SigCont);
        using (var certificate = environment.Certificate("tenant-a", "streamer-server", "probe-at-s7b"))
        using (var channel = environment.Channel(streamerPortA, certificate, "streamer-server"))
        {
            var session = new StreamerSession.StreamerSessionClient(channel);
            var stale = await Assert.ThrowsAsync<RpcException>(async () => await session.GameplayStatusAsync(
                new GameplayStatusRequest { AccessCredential = mounted.AccessCredential }));
            Assert.Equal(StatusCode.PermissionDenied, stale.StatusCode);
        }

        // The resumed session keeps working through its new holder.
        using (var certificate = environment.Certificate("tenant-a", "streamer-server", "probe-b-at-s7b"))
        using (var channel = environment.Channel(streamerPortB, certificate, "streamer-server"))
        {
            var session = new StreamerSession.StreamerSessionClient(channel);
            var stillMounted = await session.GameplayStatusAsync(new GameplayStatusRequest
            {
                AccessCredential = accessAfterResume
            });
            Assert.Equal(mounted.BindingId, stillMounted.BindingId);
        }
    }

    [Fact]
    public async Task ALateClaimCannotFlipRoutingOnceTheSessionMovedOnButANewLoginCan()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var streamerPortA = RedesignM0HostTests.HostEnvironment.FreePort();
        var streamerPortB = RedesignM0HostTests.HostEnvironment.FreePort();
        var controlEndpoint = $"https://localhost:{environment.ControlPort}";

        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort,
            "control-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                ["CHORUS_MATCH_INTERVAL_MS"] = "200",
                ["CHORUS_OWNERSHIP_RENEW_INTERVAL_MS"] = "500",
                ["CHORUS_OWNERSHIP_LEASE_SECONDS"] = "6",
                ["CHORUS_OWNERSHIP_GATE_SECONDS"] = "3"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        StreamerEnvironment StreamerEnv(int port, string slotId) => new(
            environment.DataPort, controlEndpoint, port, slotId);
        await environment.StartAsync("Chorus.StreamerServer", streamerPortA, "streamer-server",
            StreamerEnv(streamerPortA, "node-a/streamer-server").Environment(simulatorKey, accessKey),
            instanceId: "streamer-a");
        await environment.StartAsync("Chorus.StreamerServer", streamerPortB, "streamer-server",
            StreamerEnv(streamerPortB, "node-a/streamer-server#2").Environment(simulatorKey, accessKey),
            instanceId: "streamer-b");

        using var controlCertificate = environment.Certificate("@cluster", "control-server", "control-a02");
        using var controlChannel = environment.Channel(environment.ControlPort, controlCertificate, "control-server");
        var control = new ControlService.ControlServiceClient(controlChannel);
        var routing = new RoutingService.RoutingClient(controlChannel);

        using (var agentCertificate = environment.Certificate("@cluster", "node-agent", "agent-a02"))
        using (var agentChannel = environment.Channel(environment.ControlPort, agentCertificate, "control-server"))
        {
            await new ControlService.ControlServiceClient(agentChannel).RegisterNodeAsync(
                new RegisterNodeRequest { NodeId = "node-a", AgentInstanceId = "agent-a02" });
        }

        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        // The first login lands on A and mounts, so the entity is on A's record (version 1)
        // and the routing snapshot carries a streamer row to follow.
        LoginReadyReply first;
        using (var certificate = environment.Certificate("tenant-a", "streamer-server", "login1-a02"))
        using (var channel = environment.Channel(streamerPortA, certificate, "streamer-server"))
        {
            var session = new StreamerSession.StreamerSessionClient(channel);
            first = await session.LoginAsync(new LoginRequest
            {
                Platform = "simulator", AppId = "app-1",
                LaunchCredential = simulator.IssueCredential("app-1", "open-a02", "live-a02",
                    TimeSpan.FromMinutes(10)),
                LoginAttemptId = "attempt1-a02",
                NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                GatewayInstanceId = "gateway-1", ConnectionId = "connection-a02-a"
            });
            Assert.Equal(1L, first.SessionVersion);
            await session.StartGameplayAsync(new StartGameplayRequest
            {
                AccessCredential = first.AccessCredential, GameId = "game-a", OperationId = "mount-a02"
            });
        }
        var onA = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = first.StreamerId
            });
            return ownership.Found && ownership.OwnerInstanceId == "streamer-a" && ownership.Status == "Active";
        }, TimeSpan.FromSeconds(15));
        Assert.True(onA, "The first login host never claimed the streamer entity ownership.");

        // A second login through B takes over by rotation: the grant there (version 2) is the
        // current session, and B's claim naming it moves the record to B at a bumped epoch.
        using (var certificate = environment.Certificate("tenant-a", "streamer-server", "login2-a02"))
        using (var channel = environment.Channel(streamerPortB, certificate, "streamer-server"))
        {
            await new StreamerSession.StreamerSessionClient(channel).LoginAsync(new LoginRequest
            {
                Platform = "simulator", AppId = "app-1",
                LaunchCredential = simulator.IssueCredential("app-1", "open-a02", "live-a02",
                    TimeSpan.FromMinutes(10)),
                LoginAttemptId = "attempt2-a02",
                NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                GatewayInstanceId = "gateway-1", ConnectionId = "connection-a02-b"
            });
        }
        var onB = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = first.StreamerId
            });
            return ownership.Found && ownership.OwnerInstanceId == "streamer-b" && ownership.Status == "Active";
        }, TimeSpan.FromSeconds(15));
        Assert.True(onB, "The takeover login host never claimed the streamer entity ownership.");
        var epochOnB = (await control.GetOwnershipAsync(new OwnershipLookup
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = first.StreamerId
        })).Epoch;

        // A's late claim — the exact call its registrar would retry — names the session the
        // rotation retired: the control plane refuses it, and neither the record nor the
        // routing projection moves. A session-less claim never gets that far.
        using (var lateCertificate = environment.Certificate("tenant-a", "streamer-server", "streamer-a"))
        using (var lateChannel = environment.Channel(environment.ControlPort, lateCertificate, "control-server"))
        {
            var lateClient = new ControlService.ControlServiceClient(lateChannel);
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await lateClient.ClaimOwnershipAsync(
                new OwnershipRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = first.StreamerId,
                    OwnerInstanceId = "streamer-a", LeaseSeconds = 15,
                    SessionId = first.SessionId, SessionVersion = first.SessionVersion
                }));
            Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
            var noSession = await Assert.ThrowsAsync<RpcException>(async () => await lateClient.ClaimOwnershipAsync(
                new OwnershipRequest
                {
                    TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = first.StreamerId,
                    OwnerInstanceId = "streamer-a", LeaseSeconds = 15
                }));
            Assert.Equal(StatusCode.InvalidArgument, noSession.StatusCode);
        }
        var afterLate = await control.GetOwnershipAsync(new OwnershipLookup
        {
            TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = first.StreamerId
        });
        Assert.Equal("streamer-b", afterLate.OwnerInstanceId);
        Assert.Equal(epochOnB, afterLate.Epoch);
        var routeAfterLate = await routing.GetRoutingSnapshotAsync(new Empty());
        var routeEntry = Assert.Single(routeAfterLate.Streamers, entry => entry.StreamerId == first.StreamerId);
        Assert.Equal("streamer-b", routeEntry.OwnerInstanceId);

        // A legitimate new login through A rotates the session again (version 3): its claim
        // names the current session, so the record legitimately returns to A.
        using (var certificate = environment.Certificate("tenant-a", "streamer-server", "login3-a02"))
        using (var channel = environment.Channel(streamerPortA, certificate, "streamer-server"))
        {
            await new StreamerSession.StreamerSessionClient(channel).LoginAsync(new LoginRequest
            {
                Platform = "simulator", AppId = "app-1",
                LaunchCredential = simulator.IssueCredential("app-1", "open-a02", "live-a02",
                    TimeSpan.FromMinutes(10)),
                LoginAttemptId = "attempt3-a02",
                NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                GatewayInstanceId = "gateway-1", ConnectionId = "connection-a02-c"
            });
        }
        var backOnA = await WaitForAsync(async () =>
        {
            var ownership = await control.GetOwnershipAsync(new OwnershipLookup
            {
                TenantId = "tenant-a", GameId = "game-a", Kind = "streamer", EntityId = first.StreamerId
            });
            return ownership.Found && ownership.OwnerInstanceId == "streamer-a" && ownership.Status == "Active";
        }, TimeSpan.FromSeconds(15));
        Assert.True(backOnA, "A legitimate new login never took the entity back.");
    }

    private sealed record StreamerEnvironment(
        int DataPort, string ControlEndpoint, int Port, string SlotId)
    {
        public Dictionary<string, string> Environment(byte[] simulatorKey, byte[] accessKey) => new()
        {
            ["CHORUS_DATA_PORT"] = DataPort.ToString(CultureInfo.InvariantCulture),
            ["CHORUS_CONTROL_ENDPOINT"] = ControlEndpoint,
            ["CHORUS_REPORT_ENDPOINT"] = $"https://localhost:{Port}",
            ["CHORUS_SLOT_ID"] = SlotId,
            ["CHORUS_REPORT_INTERVAL_MS"] = "500",
            ["CHORUS_STREAMER_LEASE_SECONDS"] = "6",
            ["CHORUS_PLATFORM_SIMULATOR"] = "true",
            ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
            ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
            ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
        };
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
}
