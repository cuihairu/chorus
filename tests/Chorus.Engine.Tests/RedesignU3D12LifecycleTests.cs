using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Chorus.Rpc;
using Chorus.UnitySdk;
using Chorus.UnitySdk.Edge;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The lifecycle legs (FIX-U3D-12): the park notice is the edge wire's own roomSuspended
/// lane — the game handles it, the applier never sees it — and it answers a resumed
/// client's re-entry when the seat's round stands parked, the same way it ends a
/// subscription it reaches mid-play; the resume-ready declaration is the startGameplay
/// the login already ran sent again (its refusal while the seat's own round stands
/// paused IS the SYNC-08 readiness report), and the normal exits are the round leave and
/// the logout that ends the session for good. The orchestration is pinned against a fake
/// transport; the real links run end to end: a failing module parks a room and the park
/// notice answers a resumed client's re-entry, the redeclaration's refusal lifts the
/// pause through the coordinator, and a leaver's seat is gone while a logout makes the
/// held resume secret worthless.
/// </summary>
public sealed class RedesignU3D12LifecycleTests
{
    // ---------------------------------------------------------------- orchestration

    [Fact]
    public async Task ARedeclareIsTheStartGameplayTheLoginAlreadyRan()
    {
        var transports = new List<FakeTransport>();
        var sdk = NewSdk(transports);
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", "open-1", """{"credential":"c"}""");
        var session = await sdk.ConnectAsync(
            RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, 9), adapter, CancellationToken.None);
        var transport = transports[0];

        // The redeclaration rides the session's own access credential — never the mount
        // context again — and answers a fresh binding for the game it names.
        var binding = await session.RedeclareGameplayAsync("game-a", CancellationToken.None);
        Assert.Equal("binding-2", binding);
        Assert.Equal("access-1", transport.LastAccessCredential);
        Assert.Equal("game-a", transport.LastGameId);
        Assert.StartsWith("attach-", transport.LastAttachOperationId);
        Assert.Equal(new[] { "connect", "login", "start", "attach" }, transport.Sequence);

        // A redeclaration with no game is refused before the wire; the sequence stands.
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.RedeclareGameplayAsync(" ", CancellationToken.None));
        Assert.Equal(new[] { "connect", "login", "start", "attach" }, transport.Sequence);
    }

    [Fact]
    public async Task ALeaveNamesItsRoomAndTheLogoutRidesUnacknowledged()
    {
        var transports = new List<FakeTransport>();
        var sdk = NewSdk(transports);
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", "open-1", """{"credential":"c"}""");
        var session = await sdk.ConnectAsync(
            RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, 9), adapter, CancellationToken.None);
        var transport = transports[0];

        await session.LeaveRoundAsync("room-9", CancellationToken.None);
        Assert.Equal("room-9", transport.LastLeaveRoomId);
        var firstLeave = transport.LastLeaveOperationId;
        Assert.StartsWith("leave-", firstLeave);
        // The logout: the session hands the whole leg to the transport — the frame's send
        // and the wire's own close are one move on the link that owns them.
        await session.LogoutAsync(CancellationToken.None);
        Assert.StartsWith("logout-", transport.LastLogoutOperationId);
        Assert.Equal(new[] { "connect", "login", "start", "leave", "logout" }, transport.Sequence);

        // A leave with no room is refused before the wire; a second leave generates a
        // fresh operation id.
        await Assert.ThrowsAsync<ArgumentException>(() => session.LeaveRoundAsync("", CancellationToken.None));
        await session.LeaveRoundAsync("room-9", CancellationToken.None);
        Assert.NotEqual(firstLeave, transport.LastLeaveOperationId);
        Assert.StartsWith("leave-", transport.LastLeaveOperationId);
        Assert.Equal(new[] { "connect", "login", "start", "leave", "logout", "leave" },
            transport.Sequence);
    }

    // ---------------------------------------------------------------- the real park

    [Fact]
    public async Task ASuspendedRoomAnswersTheReentryWithTheParkNotice()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        // The round-tests cluster: it always starts the game host the hand-started round
        // runs on (the matchmaker cluster starts one only for its own matches).
        var (wssPort, simulator) = await RedesignM4HostRoundTests.StartEdgeClusterAsync(environment);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        var session = await LoginAndStartAsync(wssPort, simulator, "open-u3d12-susp", certs);

        // A hand-started round whose module fails deterministically (the documented fault
        // seam): the room parks visibly instead of dying, like the M4 host fact's room.
        var room = "room-u3d12-susp";
        var epoch = await RedesignM4HostRoundTests.StartFailingRoundAsync(environment, room,
            session.BindingId!, session.Login.StreamerId);
        await session.DisconnectAsync();

        // 存活性等待（事件不变量），不是性能断言：被停机的房间要先过 15s 所有权门与 5s
        // 恢复扫掠的默认节拍，再经路由传播到边缘（M4 停机同款窗口）。复入的回答与停机
        // 通告赛跑——无论哪个词先到，房间在这个线上的故事都以停机通告收尾，带着恢复将
        // 落到的纪元。
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var resumed = await ResumeAsync(wssPort, certs, session.Login.SessionId,
            session.ResumeCredential, room, 0, budget.Token);
        ISyncStream? stream = null;
        var park = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var rejoined = await resumed.AwaitRejoinedRoomAsync(budget.Token);
            Assert.Equal(room, rejoined.RoomId);
            // The join won the race: the park rides the subscription — the game checks
            // the kind before its applier (the park notice never reaches it), and the
            // notice is the last frame the stream carries.
            stream = await resumed.SubscribeAsync(rejoined.RoomId, rejoined.RoomEpoch, 0, 0, budget.Token);
            var applier = new SyncApplier(rejoined.RoomId, rejoined.RoomEpoch, DeltaContractMode.Full);
            while (await stream.MoveNextAsync(budget.Token))
            {
                if (stream.Current.Kind == SyncFrameKind.Suspended)
                {
                    park.TrySetResult(stream.Current.Suspended!.RecoveryAttempt);
                    break;
                }
                applier.Decide(stream.Current);
            }
        }
        catch (EdgeProtocolException e) when (e.Code == "roomSuspended")
        {
            // The park beat the join: the notice is the re-entry's refusal.
            park.TrySetResult(e.RecoveryAttempt);
        }
        Assert.Equal(epoch, await park.Task.WaitAsync(budget.Token));
        if (stream is not null)
            Assert.False(await stream.MoveNextAsync(budget.Token));

        await resumed.DisconnectAsync();
    }

    [Fact]
    public async Task ARedeclareOnAParkedRoundRefusesAndTheParkStands()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        // The round-tests cluster: it always starts the game host the hand-started round
        // runs on (the matchmaker cluster starts one only for its own matches).
        var (wssPort, simulator) = await RedesignM4HostRoundTests.StartEdgeClusterAsync(environment);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        var session = await LoginAndStartAsync(wssPort, simulator, "open-u3d12-ready", certs);

        var room = "room-u3d12-ready";
        await RedesignM4HostRoundTests.StartFailingRoundAsync(environment, room,
            session.BindingId!, session.Login.StreamerId);
        await session.DisconnectAsync();

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var resumed = await ResumeAsync(wssPort, certs, session.Login.SessionId,
            session.ResumeCredential, room, 0, budget.Token);
        // The re-entry races the park like the suspended fact pins; either word says the
        // same thing — the seat's round stands parked.
        try
        {
            await resumed.AwaitRejoinedRoomAsync(budget.Token);
        }
        catch (EdgeProtocolException e) when (e.Code == "roomSuspended")
        {
            // The park beat the join: the notice is the re-entry's refusal.
        }

        // The redeclaration lands its readiness only on a PAUSED round (the offline
        // pause), so the fact waits out this seam's park — a failed recovery writes
        // Suspended: 存活性等待，同 15s 所有权门与 5s 恢复扫掠的默认节拍。
        using var dataCertificate = environment.Certificate("@cluster", "control-server", "u3d12-ready-data");
        using var dataChannel = environment.Channel(environment.DataPort, dataCertificate, "data-server");
        var rounds = new RoundStore.RoundStoreClient(dataChannel);
        var parkedBy = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (true)
        {
            var round = await rounds.GetRoundAsync(new GetRoundRequest
            {
                RoomId = room, GameId = "game-a", TenantId = "tenant-a"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: budget.Token);
            if (round.Status is not "Running" and not "Preparing") break;
            Assert.True(DateTime.UtcNow < parkedBy, $"the room never parked (status {round.Status}).");
            await Task.Delay(1000, budget.Token);
        }

        // The declaration: the startGameplay the login already ran, sent again. The seat
        // holds a live, non-terminal round, so the mount is refused — the wire answers
        // with its one word for every precondition failure (the FailedPrecondition
        // conflation: the game tells this park refusal from a displacement by the
        // re-entry's park notice it just read). On the wire's side the refusal is the
        // readiness report (SYNC-08); the store counts it only for a PAUSED round —
        // this seam's failed-recovery park stays Suspended, its way back the recovery
        // sweep, which the fact pins honestly instead of claiming a lift.
        var refused = await Assert.ThrowsAsync<EdgeProtocolException>(
            () => resumed.RedeclareGameplayAsync("game-a", budget.Token));
        Assert.Equal("session_replaced", refused.Code);
        await Task.Delay(3000, budget.Token);
        var standing = await rounds.GetRoundAsync(new GetRoundRequest
        {
            RoomId = room, GameId = "game-a", TenantId = "tenant-a"
        }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: budget.Token);
        Assert.Equal("Suspended", standing.Status);

        await resumed.DisconnectAsync();
    }

    [Fact]
    public async Task ALeaveWithdrawsTheSeatAndLogoutEndsTheSession()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d12-leave-a", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d12-leave-b", certs);
        await first.JoinMatchAsync("standard", null, CancellationToken.None);
        await second.JoinMatchAsync("standard", null, CancellationToken.None);
        var room = await first.AwaitRoomAsync(CancellationToken.None);
        await second.AwaitRoomAsync(CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var stream = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);
        var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
        // The pump owns the downlink: the leave rides out, and the refusal of the leaver's
        // next intent is answered through it.
        var pump = PumpAsync(stream, applier, budget.Token);

        // The normal exit from play: the wire acknowledges no leave — the call completes
        // when the frame is sent.
        await first.LeaveRoundAsync(room.RoomId, budget.Token);

        // The seat is gone: the next intent is refused, per command, on the stream — the
        // withdrawal is fire-and-forget server-side, so the fact waits out the store's
        // write instead of racing it (存活性等待). The round runs on for everyone else.
        var refused = false;
        while (!refused)
        {
            try
            {
                await first.SendIntentAsync(new ClientIntent
                {
                    RoomId = room.RoomId,
                    RoomEpoch = room.RoomEpoch,
                    CommandId = "cmd-u3d12-" + Guid.NewGuid().ToString("N"),
                    PayloadJson = """{"action":"boost","level":1}""",
                }, budget.Token);
                await Task.Delay(500, budget.Token);
            }
            catch (EdgeProtocolException)
            {
                refused = true;
            }
        }
        Assert.True(refused);

        // The normal exit from the service: the logout's revocation is durable before the
        // gateway closes, and the SDK closes its own side behind the send — the graceful
        // close handshake is the logout's receipt, the wire's own word that the frame was
        // processed.
        await first.LogoutAsync(budget.Token);

        // The session is really gone: a resume with the held secret is refused as revoked,
        // never re-logged (the M4 wire fact's word, surfaced through the SDK).
        var revoked = await Assert.ThrowsAsync<EdgeProtocolException>(() => ResumeAsync(wssPort, certs,
            first.Login.SessionId, first.ResumeCredential, room.RoomId, 0, budget.Token));
        Assert.Equal("session_revoked", revoked.Code);

        await second.DisconnectAsync();
        await SwallowAsync(pump);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The plain pump: decide every frame the room produces; the leaves and
    /// refusals of the lifecycle facts ride it.</summary>
    private static async Task PumpAsync(ISyncStream stream, SyncApplier applier,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await stream.MoveNextAsync(cancellationToken))
                applier.Decide(stream.Current);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static async Task SwallowAsync(Task pump)
    {
        try
        {
            await pump;
        }
        catch
        {
            // The disconnect ends the pump's read mid-wait; everything after the facts
            // held is teardown.
        }
    }

    private static ChorusSdk NewSdk(List<FakeTransport> transports) => new(_ =>
    {
        var transport = new FakeTransport();
        transports.Add(transport);
        return transport;
    });

    private static SdkConfiguration Configuration(int gatewayPort) => new()
    {
        Platform = "simulator",
        AppId = "app-1",
        Tenant = "tenant-a",
        GameId = "game-a",
        Endpoint = $"https://localhost:{gatewayPort}",
        Role = "streamer",
    };

    private static async Task<ChorusSession> LoginAndStartAsync(int wssPort,
        Chorus.Platform.DevelopmentPlatformSimulator simulator, string openId,
        RemoteCertificateValidationCallback certs)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(5));
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", openId, credential);
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration, certs));
        return await sdk.ConnectAsync(
            RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, wssPort), adapter, CancellationToken.None);
    }

    private static Task<ChorusSession> ResumeAsync(int wssPort, RemoteCertificateValidationCallback certs,
        string sessionId, byte[] secret, string? lastSeenRoomId, long lastAppliedStateVersion,
        CancellationToken cancellationToken)
    {
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration, certs));
        return sdk.ResumeAsync(Configuration(wssPort), sessionId, secret, lastSeenRoomId,
            lastAppliedStateVersion, cancellationToken);
    }

    private static RemoteCertificateValidationCallback Trusts(string rootPem, string nodeAuthorityPem)
    {
        // The certs are NOT disposed: the closure must keep them alive for every
        // handshake this transport runs.
        var root = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(rootPem);
        var nodeAuthority = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            nodeAuthorityPem);
        return (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var candidate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
            using var validator = new System.Security.Cryptography.X509Certificates.X509Chain();
            validator.ChainPolicy.TrustMode =
                System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
            validator.ChainPolicy.RevocationMode =
                System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
            validator.ChainPolicy.CustomTrustStore.Add(root);
            validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
            return validator.Build(candidate);
        };
    }

    /// <summary>The transport the lifecycle-orchestration facts control: every leg is
    /// recorded, nothing answers but the login and the mount.</summary>
    private sealed class FakeTransport : IChorusTransport
    {
        public List<string> Sequence { get; } = new();
        public string? LastAccessCredential { get; private set; }
        public string? LastGameId { get; private set; }
        public string? LastAttachOperationId { get; private set; }
        public string? LastLeaveRoomId { get; private set; }
        public string? LastLeaveOperationId { get; private set; }
        public string? LastLogoutOperationId { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            Sequence.Add("connect");
            return Task.CompletedTask;
        }

        public Task<LoginAnswer> LoginAsync(LoginIntent intent, CancellationToken cancellationToken)
        {
            Sequence.Add("login");
            return Task.FromResult(new LoginAnswer
            {
                StreamerId = "streamer-1",
                SessionId = "session-1",
                SessionVersion = 1,
                AccessCredential = "access-1",
                AccessExpiresAtUnixMs = 1,
                Duplicate = false,
                ResumeRegistered = true,
            });
        }

        public Task<string> StartGameplayAsync(string accessCredential, string gameId, string operationId,
            CancellationToken cancellationToken)
        {
            if (!_mounted)
            {
                // The mount the login's connect ran (the first start on this wire).
                _mounted = true;
                Sequence.Add("start");
                return Task.FromResult("binding-1");
            }
            Sequence.Add("attach");
            LastAccessCredential = accessCredential;
            LastGameId = gameId;
            LastAttachOperationId = operationId;
            return Task.FromResult("binding-2");
        }

        private bool _mounted;

        public Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the lifecycle facts never join a match.");

        public Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the lifecycle facts never cancel a match.");

        public Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the lifecycle facts never await a room.");

        public Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the lifecycle facts never subscribe.");

        public Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the lifecycle facts never send intents.");

        public Task<LoginAnswer> ResumeAsync(ResumeIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the lifecycle facts never resume.");

        public Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the lifecycle facts never await a re-entry.");

        public Task LeaveRoundAsync(string roomId, string operationId, CancellationToken cancellationToken)
        {
            Sequence.Add("leave");
            LastLeaveRoomId = roomId;
            LastLeaveOperationId = operationId;
            return Task.CompletedTask;
        }

        public Task LogoutAsync(string operationId, CancellationToken cancellationToken)
        {
            Sequence.Add("logout");
            LastLogoutOperationId = operationId;
            return Task.CompletedTask;
        }

        public Task<LeaderboardAnswer> LeaderboardQueryAsync(LeaderboardQueryIntent intent,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the lifecycle facts never read a leaderboard.");

        public Task CloseAsync()
        {
            Sequence.Add("close");
            return Task.CompletedTask;
        }
    }
}
