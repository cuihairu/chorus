using System.Security.Cryptography;
using Google.Protobuf;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;
using Xunit;
using static Chorus.Engine.Tests.DataServerGrpcHandlerHostTests;

namespace Chorus.Engine.Tests;

/// <summary>
/// The StreamerServer-facing identity boundary and the leaderboard module boundary over the
/// same in-process DataServer host: the login/resume/binding lifecycle the gateways drive,
/// the frame-effect source read and the idempotent score projection the ControlServer
/// executor drives. CommitFrame seeds the score effect through the real frame handler so
/// the leaderboard reads its source from the ledger, not from a SQL backdoor.
/// </summary>
public sealed class DataServerIdentityLeaderboardHandlerHostTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";
    private HandlerHost _host = null!;

    public async Task InitializeAsync() => _host = await HandlerHost.CreateAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static StreamerIdentity.StreamerIdentityClient Identity(Grpc.Core.ChannelBase channel) =>
        new(channel);

    // ---- the identity lifecycle the gateways drive ----

    [Fact]
    public async Task ResolveLoginAndReadBackTheSessionTenant()
    {
        using var channel = _host.Channel(Tenant, "streamer-server", "identity-1");
        var identity = Identity(channel);
        var resolved = await identity.ResolveIdentityAsync(new ResolveIdentityRequest
        {
            TenantId = Tenant, Platform = "simulator", AppId = "app-1", OpenId = "identity-open-1"
        });
        Assert.NotEmpty(resolved.StreamerId);

        var grant = await identity.LoginAsync(new SessionLoginRequest
        {
            TenantId = Tenant, StreamerId = resolved.StreamerId,
            NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        });
        Assert.Equal(resolved.StreamerId, grant.StreamerId);
        Assert.NotEmpty(grant.SessionId);
        Assert.True(grant.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var tenant = await identity.GetSessionTenantAsync(new SessionTenantRequest
        {
            SessionId = grant.SessionId
        });
        Assert.Equal(Tenant, tenant.TenantId);
        Assert.Equal(resolved.StreamerId, tenant.StreamerId);

        var state = await identity.GetSessionStateAsync(new SessionStateRequest
        {
            TenantId = Tenant, SessionId = grant.SessionId
        });
        Assert.Equal(resolved.StreamerId, state.StreamerId);
        Assert.False(state.Revoked);
        Assert.False(state.Expired);
    }

    [Fact]
    public async Task Login_ForeignTenantIsPermissionDenied()
    {
        using var channel = _host.Channel(Tenant, "streamer-server", "identity-2");
        var identity = Identity(channel);
        var resolved = await identity.ResolveIdentityAsync(new ResolveIdentityRequest
        {
            TenantId = Tenant, Platform = "simulator", AppId = "app-1", OpenId = "identity-open-2"
        });
        var error = await Assert.ThrowsAsync<RpcException>(async () => await identity.LoginAsync(
            new SessionLoginRequest
            {
                TenantId = "tenant-b", StreamerId = resolved.StreamerId,
                NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
            }));
        Assert.Equal(StatusCode.PermissionDenied, error.Status.StatusCode);
    }

    [Fact]
    public async Task Resume_RotatesTheSecretAndRejectsAStaleOne()
    {
        using var channel = _host.Channel(Tenant, "streamer-server", "identity-3");
        var identity = Identity(channel);
        var resolved = await identity.ResolveIdentityAsync(new ResolveIdentityRequest
        {
            TenantId = Tenant, Platform = "simulator", AppId = "app-1", OpenId = "identity-open-3"
        });
        var firstSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        var login = await identity.LoginAsync(new SessionLoginRequest
        {
            TenantId = Tenant, StreamerId = resolved.StreamerId, NextResumeSecret = firstSecret,
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        });

        var nextSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        var resumed = await identity.ResumeAsync(new SessionResumeRequest
        {
            TenantId = Tenant, PreviousSessionId = login.SessionId, PreviousSecret = firstSecret,
            NextSecret = nextSecret, GatewayInstanceId = "gateway-1", ConnectionId = "connection-2",
            ResumeOperationId = "resume-op-1"
        });
        Assert.NotEqual(login.SessionId, resumed.SessionId);
        Assert.Equal(login.SessionId, resumed.PreviousSessionId);

        var stale = await Assert.ThrowsAsync<RpcException>(async () => await identity.ResumeAsync(
            new SessionResumeRequest
            {
                TenantId = Tenant, PreviousSessionId = login.SessionId, PreviousSecret = firstSecret,
                NextSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)), GatewayInstanceId = "gateway-1",
                ConnectionId = "connection-3", ResumeOperationId = "resume-op-2"
            }));
        Assert.Equal(StatusCode.PermissionDenied, stale.Status.StatusCode);
    }

    [Fact]
    public async Task EnsureEntityProfile_CreatesOnceAndAnswersTheExistingVersion()
    {
        using var channel = _host.Channel(Tenant, "streamer-server", "identity-4");
        var identity = Identity(channel);
        var resolved = await identity.ResolveIdentityAsync(new ResolveIdentityRequest
        {
            TenantId = Tenant, Platform = "simulator", AppId = "app-1", OpenId = "identity-open-4"
        });
        var first = await identity.EnsureEntityProfileAsync(new EnsureProfileRequest
        {
            TenantId = Tenant, GameId = "game-a", StreamerId = resolved.StreamerId
        });
        Assert.True(first.Found);
        Assert.True(first.Version > 0);

        var second = await identity.EnsureEntityProfileAsync(new EnsureProfileRequest
        {
            TenantId = Tenant, GameId = "game-a", StreamerId = resolved.StreamerId
        });
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public async Task AttachConnection_ThenClose_AppliesOnce()
    {
        using var channel = _host.Channel(Tenant, "streamer-server", "identity-5");
        var identity = Identity(channel);
        var resolved = await identity.ResolveIdentityAsync(new ResolveIdentityRequest
        {
            TenantId = Tenant, Platform = "simulator", AppId = "app-1", OpenId = "identity-open-5"
        });
        var secret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        var login = await identity.LoginAsync(new SessionLoginRequest
        {
            TenantId = Tenant, StreamerId = resolved.StreamerId, NextResumeSecret = secret,
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        });
        var attached = await identity.AttachConnectionAsync(new SessionAttachRequest
        {
            TenantId = Tenant, SessionId = login.SessionId, OperationId = "attach-op-1",
            CurrentSecret = secret, GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        });
        Assert.Equal(login.SessionId, attached.SessionId);
        Assert.True(attached.ConnectionGeneration > 0);

        var closed = await identity.ConnectionClosedAsync(new SessionCloseRequest
        {
            TenantId = Tenant, SessionId = login.SessionId, GatewayInstanceId = "gateway-1",
            ConnectionId = "connection-1", ConnectionGeneration = attached.ConnectionGeneration
        });
        Assert.True(closed.Applied);

        var closedAgain = await identity.ConnectionClosedAsync(new SessionCloseRequest
        {
            TenantId = Tenant, SessionId = login.SessionId, GatewayInstanceId = "gateway-1",
            ConnectionId = "connection-1", ConnectionGeneration = attached.ConnectionGeneration
        });
        Assert.True(closedAgain.Applied);
    }

    [Fact]
    public async Task OpenLiveStartBindingAndRevokeTheFullMount()
    {
        using var channel = _host.Channel(Tenant, "streamer-server", "identity-6");
        var identity = Identity(channel);
        var resolved = await identity.ResolveIdentityAsync(new ResolveIdentityRequest
        {
            TenantId = Tenant, Platform = "simulator", AppId = "app-1", OpenId = "identity-open-6"
        });
        var secret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        var login = await identity.LoginAsync(new SessionLoginRequest
        {
            TenantId = Tenant, StreamerId = resolved.StreamerId, NextResumeSecret = secret,
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-1"
        });
        var live = await identity.OpenLiveSessionAsync(new OpenLiveRequest
        {
            TenantId = Tenant, StreamerId = resolved.StreamerId, Platform = "simulator",
            AppId = "app-1", PlatformRoomKey = "platform-room",
            CanonicalLiveSessionKey = "live-mount-6"
        });
        Assert.NotEmpty(live.LiveSessionId);

        var binding = await identity.StartGameplayBindingAsync(new BindingStartRequest
        {
            TenantId = Tenant, LiveSessionId = live.LiveSessionId, SessionId = login.SessionId,
            GameId = "game-a", OperationId = "mount-op-6"
        });
        Assert.NotEmpty(binding.BindingId);
        Assert.Equal(live.LiveSessionId, binding.LiveSessionId);
        Assert.False(binding.Duplicate);

        var status = await identity.GetBindingStatusAsync(new BindingStatusRequest
        {
            TenantId = Tenant, StreamerId = resolved.StreamerId
        });
        Assert.Equal(binding.BindingId, status.BindingId);
        Assert.Equal("game-a", status.GameId);

        var liveReadBack = await identity.GetLiveSessionAsync(new LiveSessionRequest
        {
            TenantId = Tenant, StreamerId = resolved.StreamerId
        });
        Assert.Equal(live.LiveSessionId, liveReadBack.LiveSessionId);

        var revoked = await identity.RevokeSessionAsync(new SessionRevokeRequest
        {
            TenantId = Tenant, SessionId = login.SessionId
        });
        Assert.True(revoked.Applied);
        Assert.Equal(resolved.StreamerId, revoked.StreamerId);

        var missing = await Assert.ThrowsAsync<RpcException>(async () => await identity.RevokeSessionAsync(
            new SessionRevokeRequest { TenantId = Tenant, SessionId = "no-such-session" }));
        Assert.Equal(StatusCode.NotFound, missing.Status.StatusCode);
    }

    [Fact]
    public async Task IdentityBoundary_RejectsUnauthorizedRoles()
    {
        using var channel = _host.Channel("@cluster", "game-server", "identity-intruder");
        var error = await Assert.ThrowsAsync<RpcException>(async () => await Identity(channel).ReadyAsync(new Empty()));
        Assert.Equal(StatusCode.PermissionDenied, error.Status.StatusCode);
    }

    // ---- the leaderboard module boundary over the committed effect ledger ----

    [Fact]
    public async Task CommitFrame_AdvancesTheTickAndReplaysAsDuplicate()
    {
        var seeding = await SeedRunningRoundAsync("room-frame-1", "frame-open-1");
        using var channel = _host.Channel(Tenant, "game-server", "frame-1");
        var frames = new FrameStore.FrameStoreClient(channel);
        var receipt = await frames.CommitFrameAsync(CommitRequest(seeding, "frame-commit-1"));
        Assert.Equal("frame-commit-1", receipt.CommitId);
        Assert.Equal(1, receipt.Tick);
        Assert.False(receipt.Duplicate);

        var replay = await frames.CommitFrameAsync(CommitRequest(seeding, "frame-commit-1"));
        Assert.True(replay.Duplicate);
        Assert.Equal(1, replay.Tick);

        var inbox = await frames.ReadInboxAsync(new ReadInboxRequest
        {
            RoomId = "room-frame-1", GameId = "game-a", MaxEvents = 10
        });
        Assert.Empty(inbox.Items);
    }

    [Fact]
    public async Task TheCommittedEffectFeedsTheLeaderboardProjection()
    {
        var seeding = await SeedRunningRoundAsync("room-leader-1", "leader-open-1");
        var effectId = await SeedScoreEffectAsync(seeding);
        using var controlChannel = _host.Channel("@cluster", "control-server", "leader-1");
        var leaderboard = new Leaderboard.LeaderboardClient(controlChannel);

        var source = await leaderboard.ReadCommittedScoreEffectAsync(
            new ReadCommittedScoreEffectRequest { EffectId = effectId });
        Assert.True(source.Found);
        Assert.Equal("score", source.Kind);
        Assert.Contains("delta", source.PayloadJson);

        var applied = await leaderboard.ApplyScoreEffectAsync(new ApplyScoreEffectRequest
        {
            TenantId = Tenant, GameId = "game-a", EffectId = effectId, SubjectId = seeding.StreamerId,
            Delta = 15, Board = Board()
        });
        Assert.Equal(15, applied.Score);
        Assert.False(applied.Duplicate);

        var replay = await leaderboard.ApplyScoreEffectAsync(new ApplyScoreEffectRequest
        {
            TenantId = Tenant, GameId = "game-a", EffectId = effectId, SubjectId = seeding.StreamerId,
            Delta = 15, Board = Board()
        });
        Assert.Equal(15, replay.Score);
        Assert.True(replay.Duplicate);

        var top = await leaderboard.GetTopAsync(new GetTopRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board(), MaxEntries = 10
        });
        Assert.NotEmpty(top.Entries);
        Assert.Equal(seeding.StreamerId, top.Entries[0].SubjectId);
        Assert.Equal(15, top.Entries[0].Score);

        var rank = await leaderboard.GetRankAsync(new GetRankRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board(), SubjectId = seeding.StreamerId
        });
        Assert.True(rank.Found);
        Assert.Equal(1, rank.Rank);

        var around = await leaderboard.GetAroundMeAsync(new GetAroundMeRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board(), SubjectId = seeding.StreamerId,
            Window = 3
        });
        Assert.True(around.Found);
        Assert.Contains(around.Entries, entry => entry.Entry.SubjectId == seeding.StreamerId);
    }

    [Fact]
    public async Task AdjustBoardCorrectsAndRefusesAStaleVersion()
    {
        var seeding = await SeedRunningRoundAsync("room-leader-2", "leader-open-2");
        var effectId = await SeedScoreEffectAsync(seeding);
        using var controlChannel = _host.Channel("@cluster", "control-server", "leader-2");
        var leaderboard = new Leaderboard.LeaderboardClient(controlChannel);
        await leaderboard.ApplyScoreEffectAsync(new ApplyScoreEffectRequest
        {
            TenantId = Tenant, GameId = "game-a", EffectId = effectId, SubjectId = seeding.StreamerId,
            Delta = 15, Board = Board()
        });
        var top = await leaderboard.GetTopAsync(new GetTopRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board(), MaxEntries = 10
        });

        var adjusted = await leaderboard.AdjustBoardAsync(new AdjustBoardRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board(), SubjectId = seeding.StreamerId,
            Delta = 5, ExpectedVersion = top.ReadVersion, OperationId = "gm-adjust-1",
            Reason = "handler-host evidence"
        });
        Assert.Equal(20, adjusted.Score);
        Assert.False(adjusted.Duplicate);
        Assert.NotEmpty(adjusted.Version);

        var stale = await Assert.ThrowsAsync<RpcException>(async () => await leaderboard.AdjustBoardAsync(
            new AdjustBoardRequest
            {
                TenantId = Tenant, GameId = "game-a", Board = Board(), SubjectId = seeding.StreamerId,
                Delta = 5, ExpectedVersion = top.ReadVersion, OperationId = "gm-adjust-2",
                Reason = "stale read"
            }));
        Assert.Equal(StatusCode.Aborted, stale.Status.StatusCode);
    }

    [Fact]
    public async Task AdjustBoard_IsControlOnlyAndAnUncommittedEffectIsRejected()
    {
        var seeding = await SeedRunningRoundAsync("room-leader-3", "leader-open-3");
        using var gameChannel = _host.Channel("@cluster", "game-server", "leader-3");
        var error = await Assert.ThrowsAsync<RpcException>(async () => await
            new Leaderboard.LeaderboardClient(gameChannel).AdjustBoardAsync(new AdjustBoardRequest
            {
                TenantId = Tenant, GameId = "game-a", Board = Board(),
                SubjectId = seeding.StreamerId, Delta = 1, OperationId = "gm-adjust-3",
                Reason = "wrong role"
            }));
        Assert.Equal(StatusCode.PermissionDenied, error.Status.StatusCode);

        using var controlChannel = _host.Channel("@cluster", "control-server", "leader-3b");
        var rejected = await Assert.ThrowsAsync<RpcException>(async () => await
            new Leaderboard.LeaderboardClient(controlChannel).ApplyScoreEffectAsync(
                new ApplyScoreEffectRequest
                {
                    TenantId = Tenant, GameId = "game-a", EffectId = "room-leader-3/9/9",
                    SubjectId = seeding.StreamerId, Delta = 15, Board = Board()
                }));
        Assert.Equal(StatusCode.FailedPrecondition, rejected.Status.StatusCode);
    }

    [Fact]
    public async Task ResetAndRebuildReproduceTheLedger()
    {
        var seeding = await SeedRunningRoundAsync("room-leader-4", "leader-open-4");
        var effectId = await SeedScoreEffectAsync(seeding);
        using var controlChannel = _host.Channel("@cluster", "control-server", "leader-4");
        var leaderboard = new Leaderboard.LeaderboardClient(controlChannel);
        await leaderboard.ApplyScoreEffectAsync(new ApplyScoreEffectRequest
        {
            TenantId = Tenant, GameId = "game-a", EffectId = effectId, SubjectId = seeding.StreamerId,
            Delta = 15, Board = Board()
        });
        var top = await leaderboard.GetTopAsync(new GetTopRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board(), MaxEntries = 10
        });
        var reset = await leaderboard.ResetBoardAsync(new ResetBoardRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board(), SubjectId = seeding.StreamerId,
            ExpectedVersion = top.ReadVersion, OperationId = "gm-reset-1",
            Reason = "handler-host evidence"
        });
        Assert.False(reset.Duplicate);

        var rebuilt = await leaderboard.RebuildBoardAsync(new RebuildBoardRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board()
        });
        var after = await leaderboard.GetRankAsync(new GetRankRequest
        {
            TenantId = Tenant, GameId = "game-a", Board = Board(), SubjectId = seeding.StreamerId
        });
        Assert.False(after.Found);
    }

    // ---- shared arrange ----

    private static BoardDefinitionProto Board() => new()
    {
        BoardId = "board-points", Subject = (int)RankSubject.Streamer,
        Aggregation = (int)RankAggregation.Sum, PeriodKind = (int)RankPeriodKind.AllTime,
        PeriodId = "all-time", BoundsMin = -1000000, BoundsMax = 1000000,
        BoundsAllowNegative = true
    };

    private static CommitFrameRequest CommitRequest((long Epoch, string RoomId, string StreamerId) seeding,
        string commitId) => new()
    {
        RoomId = seeding.RoomId, GameId = "game-a", OwnerInstanceId = "gs-1",
        Epoch = seeding.Epoch, CommitId = commitId, PrevTick = 0, PrevStateVersion = 0,
        StateHash = ByteString.CopyFrom(SHA256.HashData("handler-host"u8.ToArray())),
        ModuleVersion = "1.0.0", ConfigHash = "cfg-handler-host",
        Effects = { new FrameEffectProto { Kind = "score", PayloadJson = """{"delta":15}""" } }
    };

    /// <summary>Mounts a streamer, claims the room, admits the round through the RPC and
    /// flips the room to Running — the state CommitFrame and the leaderboard source read
    /// both require.</summary>
    private async Task<(long Epoch, string RoomId, string StreamerId)> SeedRunningRoundAsync(
        string roomId, string openId)
    {
        var mount = await _host.MountAsync(openId, "live-" + openId);
        var epoch = await _host.ClaimOwnership(roomId);
        using (var channel = _host.Channel("@cluster", "control-server", "seed-" + roomId))
        {
            await new RoundStore.RoundStoreClient(channel).StartRoundAsync(new StartRoundRequest
            {
                RoomId = roomId, OwnerInstanceId = "gs-1", Epoch = epoch, GameId = "game-a",
                ModuleVersion = "1.0.0", ConfigHash = "cfg-handler-host", PackageHash = "pkg-1",
                OperationId = "start-" + roomId, TenantId = Tenant
            }.Tap(request => request.Participants.Add(new RoundParticipantProto
            {
                BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
            })));
        }
        await using (var connection = new NpgsqlConnection(_host.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await using var activate = new NpgsqlCommand(
                "UPDATE game.room SET status='Running' WHERE room_id=@room", connection);
            activate.Parameters.AddWithValue("room", roomId);
            await activate.ExecuteNonQueryAsync();
        }
        return (epoch, roomId, mount.StreamerId);
    }

    /// <summary>Commits one tick with a score effect through the frame handler and returns
    /// the effect id the commit wrote to the outbox ({room}/{tick}/{index}).</summary>
    private async Task<string> SeedScoreEffectAsync((long Epoch, string RoomId, string StreamerId) seeding)
    {
        using var channel = _host.Channel(Tenant, "game-server", "seed-effect");
        await new FrameStore.FrameStoreClient(channel).CommitFrameAsync(
            CommitRequest(seeding, "seed-" + seeding.RoomId));
        await using var connection = new NpgsqlConnection(_host.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(
            "SELECT effect_id FROM infra.outbox WHERE kind='score' AND effect_id LIKE @prefix ORDER BY effect_id LIMIT 1",
            connection);
        query.Parameters.AddWithValue("prefix", seeding.RoomId + "/%");
        var effectId = await query.ExecuteScalarAsync() as string;
        Assert.False(string.IsNullOrEmpty(effectId));
        return effectId!;
    }
}

/// <summary>Local helper so the request builder stays one expression.</summary>
file static class RequestTapExtensions
{
    public static T Tap<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
