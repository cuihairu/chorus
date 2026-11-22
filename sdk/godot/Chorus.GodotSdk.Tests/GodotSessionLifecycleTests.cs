using Chorus.GodotSdk;
using Xunit;

namespace Chorus.GodotSdk.Tests;

/// <summary>
/// The Godot SDK's session lifecycle (EK-1): the login link mounts through the
/// adapter, exchanges the mount for a session, starts the gameplay binding and answers
/// a session carrying the login facts, the binding and the resume credential — and the
/// resume link rotates onto a fresh session without touching the adapter. The event
/// surface fires as the wire's facts land. A fake transport carries the wire; the
/// orchestration under test is the SDK's own.
/// </summary>
public sealed class GodotSessionLifecycleTests
{
    private static SdkConfiguration Config() => new SdkConfiguration
    {
        Platform = "douyin",
        AppId = "app-1",
        Tenant = "tenant-a",
        GameId = "game-1",
        Endpoint = "wss://gateway.example/ws",
        Role = "player",
    };

    private sealed class StubAdapter : IPlatformAdapter
    {
        public string PlatformName => "douyin";
        public PlatformMount Mount { get; set; } = new()
        {
            Platform = "douyin",
            AppId = "app-1",
            Role = "player",
            SessionContextJson = "{\"launch\":\"cred\"}",
        };
        public int MountCalls { get; private set; }
        public Task<PlatformMount> MountAsync(CancellationToken cancellationToken)
        {
            MountCalls++;
            return Task.FromResult(Mount);
        }
        public Task ShutdownAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task ConnectMountsLogsInStartsGameplayAndAnswersTheSession()
    {
        var transport = new FakeTransport();
        var sdk = new ChorusGodotSdk(_ => transport);
        GodotChorusSession? established = null;
        sdk.SessionEstablished += s => established = s;

        var session = await sdk.ConnectAsync(Config(), new StubAdapter(), default);

        Assert.True(transport.Connected);
        Assert.NotNull(transport.LastLogin);
        Assert.Equal("douyin", transport.LastLogin!.Platform);
        Assert.Equal("app-1", transport.LastLogin.AppId);
        Assert.Equal("{\"launch\":\"cred\"}", transport.LastLogin.LaunchCredential);
        Assert.StartsWith("attempt-", transport.LastLogin.LoginAttemptId);
        Assert.Equal(32, transport.LastLogin.NextResumeSecret.Length);

        Assert.Equal("sess-1", session.Login.SessionId);
        Assert.Equal("cred-1", session.Login.AccessCredential);
        Assert.Equal("bind-1", session.BindingId);
        Assert.Equal(32, session.ResumeCredential.Length);
        // The session established before the connect returned: the game subscribes in
        // the handler and misses nothing.
        Assert.Same(session, established);
    }

    [Fact]
    public async Task ConnectRefusesAMissingFieldBeforeTheAdapter()
    {
        var transport = new FakeTransport();
        var adapter = new StubAdapter();
        var sdk = new ChorusGodotSdk(_ => transport);
        var config = Config();
        config.Tenant = "";

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sdk.ConnectAsync(config, adapter, default));

        Assert.Equal(0, adapter.MountCalls);
        Assert.False(transport.Connected);
    }

    [Fact]
    public async Task ConnectRefusesAMountPlatformMismatch()
    {
        var sdk = new ChorusGodotSdk(_ => new FakeTransport());
        var adapter = new StubAdapter { Mount = new PlatformMount { Platform = "kuaishou", Role = "player" } };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sdk.ConnectAsync(Config(), adapter, default));
    }

    [Fact]
    public async Task ConnectClosesTheWireWhenTheLoginFails()
    {
        var transport = new FakeTransport { FailConnect = true, ConnectError = new InvalidOperationException("down") };
        var sdk = new ChorusGodotSdk(_ => transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sdk.ConnectAsync(Config(), new StubAdapter(), default));

        Assert.True(transport.Closed, "a half-open link must close before the failure surfaces.");
    }

    [Fact]
    public async Task ResumeRotatesOntoAFreshSessionWithoutTheAdapter()
    {
        var transport = new FakeTransport();
        var sdk = new ChorusGodotSdk(_ => transport);
        GodotChorusSession? established = null;
        sdk.SessionEstablished += s => established = s;
        var secret = new byte[32];

        var session = await sdk.ResumeAsync(Config(), "sess-0", secret, "room-1", 2, default);

        Assert.True(transport.Connected);
        Assert.NotNull(transport.LastResume);
        Assert.Equal("sess-0", transport.LastResume!.PreviousSessionId);
        Assert.Equal(secret, transport.LastResume.ResumeCredential);
        Assert.Equal("room-1", transport.LastResume.LastSeenRoomId);
        Assert.Equal(2, transport.LastResume.LastAppliedStateVersion);
        Assert.Equal(32, transport.LastResume.NextResumeSecret.Length);
        // A resumed session never restarts the gameplay binding.
        Assert.Null(session.BindingId);
        Assert.Same(session, established);
    }

    [Fact]
    public async Task ResumeRefusesAShortCredential()
    {
        var sdk = new ChorusGodotSdk(_ => new FakeTransport());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sdk.ResumeAsync(Config(), "sess-0", new byte[16], null, 0, default));
    }

    [Fact]
    public async Task TheEventSurfaceFiresAsTheFactsLand()
    {
        var transport = new FakeTransport { Ticket = new MatchTicket { TicketId = "t-1", State = "Matched", RoomId = "room-1" } };
        var sdk = new ChorusGodotSdk(_ => transport);
        var session = await sdk.ConnectAsync(Config(), new StubAdapter(), default);

        MatchTicket? joined = null;
        JoinedRoom? room = null;
        string? left = null;
        var disconnected = false;
        session.MatchJoined += t => joined = t;
        session.RoomJoined += r => room = r;
        session.RoomLeft += r => left = r;
        session.Disconnected += () => disconnected = true;

        await session.JoinMatchAsync("standard", null, default);
        Assert.NotNull(joined);
        Assert.Equal("room-1", joined!.RoomId);

        await session.AwaitRoomAsync(default);
        Assert.NotNull(room);
        Assert.Equal("room-1", room!.RoomId);

        await session.LeaveRoundAsync("room-1", default);
        Assert.Equal("room-1", left);

        await session.DisconnectAsync();
        Assert.True(disconnected);
        Assert.True(transport.Closed);
    }

    [Fact]
    public async Task SendIntentRefusesAnUnnamedRoomOrEpoch()
    {
        var transport = new FakeTransport();
        var sdk = new ChorusGodotSdk(_ => transport);
        var session = await sdk.ConnectAsync(Config(), new StubAdapter(), default);

        await Assert.ThrowsAsync<ArgumentException>(() => session.SendIntentAsync(
            new ClientIntent { RoomId = "", RoomEpoch = 3, CommandId = "c-1" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendIntentAsync(
            new ClientIntent { RoomId = "room-1", RoomEpoch = 0, CommandId = "c-1" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendIntentAsync(
            new ClientIntent { RoomId = "room-1", RoomEpoch = 3, CommandId = "" }, default));
    }

    [Fact]
    public async Task TheLeaderboardReadsMapTheWireAnswerOntoTheLeafShapes()
    {
        var transport = new FakeTransport
        {
            BoardAnswer = new LeaderboardAnswer
            {
                Found = true,
                ReadVersion = "v-9",
                NextCursor = "c-2",
                HasMore = true,
                Entries = new[]
                {
                    new LeaderboardRankedWireEntry { Rank = 1, SubjectId = "u-1", Score = 100 },
                    new LeaderboardRankedWireEntry { Rank = 2, SubjectId = "u-2", Score = 90 },
                },
            },
        };
        var sdk = new ChorusGodotSdk(_ => transport);
        var session = await sdk.ConnectAsync(Config(), new StubAdapter(), default);
        var key = new LeaderboardKey { BoardId = "board-1" };

        var top = await session.GetTopAsync(key, 10, null, default);
        Assert.Equal(2, top.Entries.Count);
        Assert.Equal("v-9", top.ReadVersion);
        Assert.Equal("c-2", top.NextCursor);
        Assert.True(top.HasMore);

        var rank = await session.GetRankAsync(key, "u-1", default);
        Assert.True(rank.Found);
        Assert.Equal(1, rank.Rank);
        Assert.Equal("u-1", rank.SubjectId);
        Assert.Equal(100, rank.Score);
    }
}
