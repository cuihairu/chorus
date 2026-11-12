using System.Security.Cryptography;
using Chorus.Platform;
using Chorus.Rpc;
using Chorus.StreamerServer;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.Engine.Tests;

/// <summary>
/// The StreamerSession boundary driven as plain method calls: the certificate fence runs its
/// real role/tenant logic against issued process certificates, and the coordinator behind it
/// runs against the same fake stores the coordinator tests use — so the tests pin the error
/// mapping and the reply shapes at the exact face the gateways call.
/// </summary>
public sealed class RedesignStreamerSessionGrpcServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20_000);

    private sealed class FakeIdentityStore : IStreamerIdentityStore
    {
        public int Logins;
        public StreamerSessionState? State = new("streamer-fake", "tenant-a", 1, "gateway-1", "connection-1",
            1, Revoked: false, Closed: false, Expired: false, Banned: false, Current: true);
        public string? SessionTenant;
        public bool LiveSessionOpen = true;
        public StreamerBinding? Binding = new("binding-fake", "live-fake", "game-a", 1, "Ready", false);
        public string? StatusRoomId;
        public long StatusRoundGeneration;
        public Exception? LoginException;
        public Exception? ResumeException;
        public Exception? StartGameplayException;
        public bool FailRecovery;

        public Task<string?> GetSessionTenantAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(SessionTenant);

        public Task<StreamerSessionRevocation> RevokeSessionAsync(string tenantId, string sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerSessionRevocation(sessionId, "streamer-fake", 1, true, null, null, 0));

        public Task<string> ResolveIdentityAsync(string tenantId, string platform, string appId, string openId,
            CancellationToken cancellationToken = default) => Task.FromResult("streamer-fake");

        public Task<StreamerSessionGrant> LoginAsync(string tenantId, string streamerId, string gatewayInstanceId,
            string connectionId, byte[] nextResumeSecret, CancellationToken cancellationToken = default)
        {
            Logins++;
            if (LoginException is not null) throw LoginException;
            return Task.FromResult(new StreamerSessionGrant(streamerId, "session-fake", 1,
                Now.UtcDateTime.AddHours(1), false));
        }

        public Task<StreamerSessionGrant> ResumeAsync(string tenantId, string previousSessionId, string gatewayInstanceId,
            string connectionId, string operationId, byte[] previousSecret, byte[] nextSecret,
            CancellationToken cancellationToken = default)
        {
            if (ResumeException is not null) throw ResumeException;
            return Task.FromResult(new StreamerSessionGrant("streamer-fake", "session-fake", 2,
                Now.UtcDateTime.AddHours(1), false));
        }

        public Task<StreamerConnection> AttachConnectionAsync(string tenantId, string sessionId,
            string gatewayInstanceId, string connectionId, string operationId, byte[] currentSecret,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerConnection(sessionId, "streamer-fake", 1, 2, false));

        public Task<bool> ConnectionClosedAsync(string tenantId, string sessionId, string gatewayInstanceId,
            string connectionId, long connectionGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<StreamerSessionState?> GetSessionStateAsync(string tenantId, string sessionId,
            CancellationToken cancellationToken = default)
            => FailRecovery
                ? throw new InvalidOperationException("storage down")
                : Task.FromResult(State);

        public Task<string> OpenLiveSessionAsync(string tenantId, string streamerId, string platform, string appId,
            string platformRoomKey, string canonicalLiveSessionKey, CancellationToken cancellationToken = default)
            => Task.FromResult("live-fake");

        public Task<string?> GetLiveSessionIdAsync(string tenantId, string streamerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(LiveSessionOpen ? "live-fake" : null);

        public Task<StreamerBinding> StartGameplayAsync(string tenantId, string liveSessionId, string sessionId,
            string gameId, string operationId, CancellationToken cancellationToken = default)
        {
            if (StartGameplayException is not null) throw StartGameplayException;
            return Task.FromResult(Binding! with { GameId = gameId });
        }

        public Task<StreamerBindingStatus?> GetActiveBindingAsync(string tenantId, string streamerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StreamerBindingStatus?>(Binding is null
                ? null
                : new StreamerBindingStatus(Binding.LiveSessionId, Binding.BindingId, Binding.GameId,
                    Binding.Generation, Binding.State, StatusRoomId, StatusRoundGeneration));
    }

    private sealed class LoadedProfileStore : IStreamerProfileStore
    {
        public Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StreamerProfile?> LoadAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StreamerProfile?>(new StreamerProfile(1, 1, "{}"));
        public Task<StreamerProfile> PatchAsync(string tenantId, string gameId, string streamerId,
            string operationId, long expectedVersion, int schemaVersion, string dataJson,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new StreamerProfile(expectedVersion + 1, schemaVersion, dataJson));
    }

    private sealed class BrokenProfileStore : IStreamerProfileStore
    {
        public Task EnsureLoadedAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("storage down");
        public Task<StreamerProfile?> LoadAsync(string tenantId, string gameId, string streamerId,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("storage down");
        public Task<StreamerProfile> PatchAsync(string tenantId, string gameId, string streamerId,
            string operationId, long expectedVersion, int schemaVersion, string dataJson,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("storage down");
    }

    private sealed class LoadedAssetStore : IStreamerAssetStore
    {
        public Task<AssetChangeResult> ChangeAsync(string tenantId, string gameId, string streamerId,
            string operationId, string assetId, long delta, string reason,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AssetChangeResult(delta, 1, Duplicate: false));
    }

    private sealed record Rig(
        StreamerSessionGrpcService Service, FakeIdentityStore Store, TestProcessCertificates Certificates,
        Func<string, string> IssueCredential)
    {
        public TestServerCallContext StreamerContext() =>
            Certificates.Context(Certificates.Issue("tenant-a", "streamer-server", "svc"));

        public TestServerCallContext ClusterContext() =>
            Certificates.Context(Certificates.Issue("@cluster", "streamer-gateway", "edge-1"));

        public async Task<LoginReadyReply> LoginAsync(string tenantId = "") =>
            await Service.Login(LoginRequestOf(IssueCredential, "app-1", tenantId), StreamerContext());
    }

    private static Rig New(IStreamerProfileStore? profile = null)
    {
        var simulator = new DevelopmentPlatformSimulator(RandomNumberGenerator.GetBytes(32), () => Now);
        var store = new FakeIdentityStore();
        var certificates = new TestProcessCertificates();
        var coordinator = new RedesignStreamerLoginCoordinator(
            new RedesignPlatformGateway([simulator], allowNotProductionReady: true),
            store,
            profile ?? new LoadedProfileStore(),
            new LoadedAssetStore(),
            new StreamerActorRegistry(),
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["tenant-a"] = new Dictionary<string, string> { ["app-1"] = "game-a" }
            },
            RandomNumberGenerator.GetBytes(32),
            () => Now);
        return new Rig(new StreamerSessionGrpcService(coordinator, certificates.Trusted), store,
            certificates, appId => simulator.IssueCredential(appId, "open-x", "live-x", TimeSpan.FromMinutes(5)));
    }

    private static LoginRequest LoginRequestOf(Func<string, string> issue, string appId, string tenantId) => new()
    {
        Platform = "simulator", AppId = appId, LaunchCredential = issue(appId), LoginAttemptId = "attempt-1",
        NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
        GatewayInstanceId = "gateway-1", ConnectionId = "connection-1", TenantId = tenantId
    };

    private static ResumeRequest ResumeRequest(string tenantId = "") => new()
    {
        PreviousSessionId = "session-fake", ResumeOperationId = "resume-1",
        PreviousSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
        NextSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
        GatewayInstanceId = "gateway-2", ConnectionId = "connection-2", TenantId = tenantId
    };

    private static AttachConnectionRequest AttachRequest() => new()
    {
        SessionId = "session-fake", OperationId = "attach-1",
        CurrentSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
        GatewayInstanceId = "gateway-2", ConnectionId = "connection-2"
    };

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        Assert.Equal(detail, error.Status.Detail);
    }

    [Fact]
    public async Task ReadyAnswersForTheStreamerGatewayAndNodeAgentRoles()
    {
        var rig = New();
        Assert.True((await rig.Service.Ready(new Empty(),
            rig.Certificates.Context(rig.Certificates.Issue("tenant-a", "streamer-server", "probe")))).Ready);
        Assert.True((await rig.Service.Ready(new Empty(), rig.ClusterContext())).Ready);
        Assert.True((await rig.Service.Ready(new Empty(),
            rig.Certificates.Context(rig.Certificates.Issue("tenant-a", "node-agent", "agent-1")))).Ready);
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheFace()
    {
        var rig = New();
        await Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.Issue("tenant-a", "game-server", "game-1"))),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task ReadyRefusesACertificateFromAForeignCluster()
    {
        var rig = New();
        await Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        var rig = New();
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task LoginMapsTheGrantIntoTheReadyReply()
    {
        var rig = New();
        var reply = await rig.LoginAsync();
        Assert.Equal("streamer-fake", reply.StreamerId);
        Assert.Equal("session-fake", reply.SessionId);
        Assert.Equal(1, reply.SessionVersion);
        Assert.False(reply.Duplicate);
        Assert.True(reply.ResumeRegistered);
        Assert.False(string.IsNullOrEmpty(reply.AccessCredential));
        Assert.Equal(Now.AddHours(1).ToUnixTimeMilliseconds(), reply.AccessExpiresAtUnixMs);
        Assert.Equal(1, rig.Store.Logins);
    }

    [Fact]
    public async Task LoginRefusesAnUnmappedApplication()
    {
        var rig = New();
        await Rejects(() => rig.Service.Login(LoginRequestOf(rig.IssueCredential, "app-other", ""),
                rig.StreamerContext()), StatusCode.PermissionDenied, "The application is not mapped for this tenant.");
    }

    [Fact]
    public async Task LoginRefusesAForgedLaunchCredential()
    {
        var rig = New();
        var request = LoginRequestOf(rig.IssueCredential, "app-1", "");
        request.LaunchCredential = "forged";
        await Rejects(() => rig.Service.Login(request, rig.StreamerContext()),
            StatusCode.PermissionDenied, "The platform launch credential is rejected.");
    }

    [Fact]
    public async Task LoginRefusesAnUnreadyEntity()
    {
        var rig = New(profile: new BrokenProfileStore());
        await Rejects(() => rig.LoginAsync(),
            StatusCode.FailedPrecondition, "The streamer entity is not ready.");
        Assert.Equal(0, rig.Store.Logins);
    }

    [Fact]
    public async Task LoginRefusesARevokedSessionGrant()
    {
        var rig = New();
        rig.Store.LoginException = new SessionRevokedException();
        await Rejects(() => rig.LoginAsync(), StatusCode.PermissionDenied, "Session is revoked.");
    }

    [Fact]
    public async Task LoginSurfacesAMalformedRequestAsInvalidArgument()
    {
        var rig = New();
        rig.Store.LoginException = new ArgumentException("gateway instance is required");
        await Rejects(() => rig.LoginAsync(), StatusCode.InvalidArgument, "The login request is invalid.");
    }

    [Fact]
    public async Task LoginForAClusterScopedCallerNamesTheTenant()
    {
        var rig = New();
        var reply = await rig.Service.Login(LoginRequestOf(rig.IssueCredential, "app-1", "tenant-a"),
            rig.ClusterContext());
        Assert.Equal("streamer-fake", reply.StreamerId);
    }

    [Fact]
    public async Task LoginRefusesATenantBeyondTheCallerScope()
    {
        var rig = New();
        await Rejects(() => rig.Service.Login(LoginRequestOf(rig.IssueCredential, "app-1", "tenant-b"),
                rig.StreamerContext()), StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    [Fact]
    public async Task ResumeMapsTheRotatedGrant()
    {
        var rig = New();
        var reply = await rig.Service.Resume(ResumeRequest(), rig.StreamerContext());
        Assert.Equal("streamer-fake", reply.StreamerId);
        Assert.Equal("session-fake", reply.SessionId);
        Assert.Equal(2, reply.SessionVersion);
        Assert.True(reply.ResumeRegistered);
    }

    [Fact]
    public async Task ResumeForAClusterScopedCallerResolvesTheSessionsTenant()
    {
        var rig = New();
        rig.Store.SessionTenant = "tenant-a";
        var reply = await rig.Service.Resume(ResumeRequest(), rig.ClusterContext());
        Assert.Equal(2, reply.SessionVersion);
    }

    [Fact]
    public async Task ResumeForAClusterScopedCallerRefusesAnUnknownSession()
    {
        var rig = New();
        await Rejects(() => rig.Service.Resume(ResumeRequest(), rig.ClusterContext()),
            StatusCode.PermissionDenied, "Session is revoked.");
    }

    [Fact]
    public async Task ResumeRefusesAReplacedSession()
    {
        var rig = New();
        rig.Store.ResumeException = new SessionReplacedException();
        await Rejects(() => rig.Service.Resume(ResumeRequest(), rig.StreamerContext()),
            StatusCode.FailedPrecondition, "Session is replaced.");
    }

    [Fact]
    public async Task ResumeRefusesAnUnrecoverableEntity()
    {
        var rig = New();
        rig.Store.FailRecovery = true;
        await Rejects(() => rig.Service.Resume(ResumeRequest(), rig.StreamerContext()),
            StatusCode.FailedPrecondition, "The streamer entity could not be recovered.");
    }

    [Fact]
    public async Task ResumeSurfacesAMalformedRequestAsInvalidArgument()
    {
        var rig = New();
        rig.Store.ResumeException = new ArgumentException("operation id is required");
        await Rejects(() => rig.Service.Resume(ResumeRequest(), rig.StreamerContext()),
            StatusCode.InvalidArgument, "The resume request is invalid.");
    }

    [Fact]
    public async Task LogoutRevokesTheActingSession()
    {
        var rig = New();
        var login = await rig.LoginAsync();
        var logout = await rig.Service.Logout(new LogoutRequest
        {
            AccessCredential = login.AccessCredential,
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        }, rig.StreamerContext());
        Assert.Equal("session-fake", logout.SessionId);
        Assert.Equal("streamer-fake", logout.StreamerId);
        Assert.Equal(1, logout.SessionVersion);
        Assert.False(logout.Duplicate);
    }

    [Fact]
    public async Task LogoutForAClusterScopedCallerResolvesTheSessionsTenant()
    {
        var rig = New();
        rig.Store.SessionTenant = "tenant-a";
        var login = await rig.Service.Login(LoginRequestOf(rig.IssueCredential, "app-1", "tenant-a"),
            rig.ClusterContext());
        var logout = await rig.Service.Logout(new LogoutRequest
        {
            AccessCredential = login.AccessCredential,
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        }, rig.ClusterContext());
        Assert.Equal("session-fake", logout.SessionId);
    }

    [Fact]
    public async Task LogoutRefusesARejectedCredential()
    {
        var rig = New();
        await Rejects(() => rig.Service.Logout(new LogoutRequest
        {
            AccessCredential = "not-a-credential",
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        }, rig.StreamerContext()), StatusCode.PermissionDenied, "The access credential is rejected.");
    }

    [Fact]
    public async Task AttachConnectionMapsTheConnectionAndRecoversTheAbsentActor()
    {
        var rig = New();
        var reply = await rig.Service.AttachConnection(AttachRequest(), rig.StreamerContext());
        Assert.Equal("session-fake", reply.SessionId);
        Assert.Equal("streamer-fake", reply.StreamerId);
        Assert.Equal(1, reply.SessionVersion);
        Assert.Equal(2, reply.ConnectionGeneration);
        Assert.False(reply.Duplicate);
    }

    [Fact]
    public async Task AttachConnectionRefusesAnUnrecoverableEntity()
    {
        var rig = New();
        rig.Store.FailRecovery = true;
        await Rejects(() => rig.Service.AttachConnection(AttachRequest(), rig.StreamerContext()),
            StatusCode.FailedPrecondition, "The streamer entity could not be recovered.");
    }

    [Fact]
    public async Task ConnectionClosedReportsTheAppliedVerdict()
    {
        var rig = New();
        var reply = await rig.Service.ConnectionClosed(new ConnectionClosedRequest
        {
            SessionId = "session-fake", GatewayInstanceId = "gateway-1",
            ConnectionId = "connection-1", ConnectionGeneration = 1
        }, rig.StreamerContext());
        Assert.True(reply.Applied);
    }

    [Fact]
    public async Task ValidateAccessAnswersTheLiveConnection()
    {
        var rig = New();
        var login = await rig.LoginAsync();
        var access = await rig.Service.ValidateAccess(new ValidateAccessRequest
        {
            AccessCredential = login.AccessCredential
        }, rig.StreamerContext());
        Assert.Equal("streamer-fake", access.StreamerId);
        Assert.Equal("session-fake", access.SessionId);
        Assert.Equal(1, access.SessionVersion);
        Assert.Equal(1, access.ConnectionGeneration);
        Assert.Equal("gateway-1", access.GatewayInstanceId);
        Assert.Equal("connection-1", access.ConnectionId);
        Assert.True(access.Online);
    }

    [Fact]
    public async Task ValidateAccessRefusesARevokedSession()
    {
        var rig = New();
        rig.Store.State = rig.Store.State! with { Revoked = true, Current = false };
        var login = await rig.LoginAsync();
        await Rejects(() => rig.Service.ValidateAccess(new ValidateAccessRequest
        {
            AccessCredential = login.AccessCredential
        }, rig.StreamerContext()), StatusCode.PermissionDenied, "The access credential is rejected.");
    }

    [Fact]
    public async Task ValidateAccessRefusesGarbage()
    {
        var rig = New();
        await Rejects(() => rig.Service.ValidateAccess(new ValidateAccessRequest
        {
            AccessCredential = "not-a-credential"
        }, rig.StreamerContext()), StatusCode.PermissionDenied, "The access credential is rejected.");
    }

    [Fact]
    public async Task StartGameplayMapsTheBinding()
    {
        var rig = New();
        var login = await rig.LoginAsync();
        var reply = await rig.Service.StartGameplay(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-1"
        }, rig.StreamerContext());
        Assert.Equal("live-fake", reply.LiveSessionId);
        Assert.Equal("binding-fake", reply.BindingId);
        Assert.Equal("game-a", reply.GameId);
        Assert.Equal(1, reply.Generation);
        Assert.Equal("Ready", reply.State);
        Assert.False(reply.Duplicate);
        Assert.Equal("", reply.CurrentRoomId);
        Assert.Equal(0, reply.RoundGeneration);
    }

    [Fact]
    public async Task StartGameplayRefusesAnUnmappedGame()
    {
        var rig = New();
        var login = await rig.LoginAsync();
        await Rejects(() => rig.Service.StartGameplay(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-other", OperationId = "mount-1"
        }, rig.StreamerContext()), StatusCode.PermissionDenied, "The application is not mapped for this tenant.");
    }

    [Fact]
    public async Task StartGameplayRefusesWhenNoLiveSessionIsOpen()
    {
        var rig = New();
        rig.Store.LiveSessionOpen = false;
        var login = await rig.LoginAsync();
        await Rejects(() => rig.Service.StartGameplay(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-1"
        }, rig.StreamerContext()), StatusCode.FailedPrecondition, "The gameplay binding is not available.");
    }

    [Fact]
    public async Task StartGameplayRefusesAMountInsideALiveRound()
    {
        var rig = New();
        rig.Store.StartGameplayException = new MountInLiveRoundException();
        var login = await rig.LoginAsync();
        await Rejects(() => rig.Service.StartGameplay(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-1"
        }, rig.StreamerContext()), StatusCode.FailedPrecondition,
            "The live session still participates in a running round; finish or leave it before mounting again.");
    }

    [Fact]
    public async Task StartGameplayRefusesARevokedSession()
    {
        var rig = New();
        rig.Store.State = rig.Store.State! with { Revoked = true, Current = false };
        var login = await rig.LoginAsync();
        await Rejects(() => rig.Service.StartGameplay(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-1"
        }, rig.StreamerContext()), StatusCode.PermissionDenied, "The access credential is rejected.");
    }

    [Fact]
    public async Task GameplayStatusAnswersTheMountsRoundParticipation()
    {
        var rig = New();
        rig.Store.StatusRoomId = "room-x";
        rig.Store.StatusRoundGeneration = 7;
        var login = await rig.LoginAsync();
        var reply = await rig.Service.GameplayStatus(new GameplayStatusRequest
        {
            AccessCredential = login.AccessCredential
        }, rig.StreamerContext());
        Assert.Equal("binding-fake", reply.BindingId);
        Assert.Equal("live-fake", reply.LiveSessionId);
        Assert.Equal("room-x", reply.CurrentRoomId);
        Assert.Equal(7, reply.RoundGeneration);
    }

    [Fact]
    public async Task GameplayStatusAnswersEmptyWhenNothingIsMounted()
    {
        var rig = New();
        rig.Store.Binding = null;
        var login = await rig.LoginAsync();
        var reply = await rig.Service.GameplayStatus(new GameplayStatusRequest
        {
            AccessCredential = login.AccessCredential
        }, rig.StreamerContext());
        Assert.Equal("", reply.BindingId);
        Assert.Equal("", reply.LiveSessionId);
    }
}
