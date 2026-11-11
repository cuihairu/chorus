using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Chorus.UnitySdk;
using Chorus.UnitySdk.Edge;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The login link (FIX-U3D-07): the SDK's connect orchestration — mount once, login
/// once, start the gameplay binding, answer the session — is pinned against a fake
/// transport (sequence, intent fields, fresh secrets, refusal and cleanup behavior),
/// the WSS derivation is pinned as a pure fact, and the real link runs end to end
/// against a real gateway process: the SDK surface, a WebSocket, the edge, the
/// streamer server — LoginReady and a started binding, on the wire.
/// </summary>
public sealed class RedesignU3D07LoginLinkTests
{
    [Fact]
    public async Task ConnectMountsThenLogsInThenStartsGameplayAndAnswersTheSession()
    {
        var transport = new FakeTransport();
        var adapter = new FakeAdapter("simulator", "open-u3d07-1", """{"credential":"c-1"}""");
        var sdk = new ChorusSdk(_ => transport);

        var session = await sdk.ConnectAsync(Configuration(adapter), adapter, CancellationToken.None);

        // The wire sequence, in order, once.
        Assert.Equal(new[] { "connect", "login", "startGameplay" }, transport.Sequence);
        // The intent carried the mount's facts and the session-security facts.
        var intent = transport.LastIntent!;
        Assert.Equal("simulator", intent.Platform);
        Assert.Equal("app-1", intent.AppId);
        Assert.Equal("""{"credential":"c-1"}""", intent.LaunchCredential);
        Assert.StartsWith("attempt-", intent.LoginAttemptId);
        Assert.Equal(32, intent.NextResumeSecret.Length);
        // The session answered the login's identities and the started binding.
        Assert.Equal("streamer-1", session.Login.StreamerId);
        Assert.Equal("session-1", session.Login.SessionId);
        Assert.Equal("binding-1", session.BindingId);
    }

    [Fact]
    public async Task EveryConnectGeneratesFreshAttemptAndSecret()
    {
        var adapter = new FakeAdapter("simulator", "open-1", """{"credential":"c"}""");
        var transports = new List<FakeTransport>();
        var sdk = new ChorusSdk(_ =>
        {
            var transport = new FakeTransport();
            transports.Add(transport);
            return transport;
        });

        await sdk.ConnectAsync(Configuration(adapter), adapter, CancellationToken.None);
        await sdk.ConnectAsync(Configuration(adapter), adapter, CancellationToken.None);

        var first = transports[0].LastIntent!;
        var second = transports[1].LastIntent!;
        Assert.NotEqual(first.LoginAttemptId, second.LoginAttemptId);
        Assert.False(first.NextResumeSecret.SequenceEqual(second.NextResumeSecret));
    }

    [Fact]
    public async Task AnIncompleteConfigurationIsRefused()
    {
        var adapter = new FakeAdapter("simulator", "open-1", """{"credential":"c"}""");
        var sdk = new ChorusSdk(_ => new FakeTransport());

        var missing = Configuration(adapter);
        missing.Tenant = "";
        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => sdk.ConnectAsync(missing, adapter, CancellationToken.None));
        Assert.Contains("tenant", refused.Message);

        var wrongSeat = Configuration(adapter);
        wrongSeat.Role = "spectator";
        await Assert.ThrowsAsync<ArgumentException>(
            () => sdk.ConnectAsync(wrongSeat, adapter, CancellationToken.None));
    }

    [Fact]
    public async Task APlatformMismatchBetweenAdapterAndConfigurationIsRefused()
    {
        var adapter = new FakeAdapter("douyin", "open-1", """{"credential":"c"}""");
        var sdk = new ChorusSdk(_ => new FakeTransport());
        var configuration = Configuration(adapter);
        configuration.Platform = "simulator";
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sdk.ConnectAsync(configuration, adapter, CancellationToken.None));
        Assert.Contains("douyin", failure.Message);
        Assert.Contains("simulator", failure.Message);
    }

    [Fact]
    public async Task AFailedLoginClosesTheTransportBeforeTheFailureSurfaces()
    {
        var adapter = new FakeAdapter("simulator", "open-1", """{"credential":"c"}""");
        var transport = new FakeTransport { LoginFailure = "the launch credential was refused." };
        var sdk = new ChorusSdk(_ => transport);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sdk.ConnectAsync(Configuration(adapter), adapter, CancellationToken.None));
        Assert.Contains("the launch credential was refused.", failure.Message);
        // A half-open link never leaks: the wire closed even though login failed.
        Assert.Equal(new[] { "connect", "login", "close" }, transport.Sequence);
    }

    [Fact]
    public async Task CancellationDuringTheMountNeverOpensTheWire()
    {
        var adapter = new CancellingAdapter();
        var sdk = new ChorusSdk(_ => new FakeTransport());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sdk.ConnectAsync(Configuration(adapter), adapter, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task TheSyncAndIntentLinksRideThroughAndDisconnectClosesTheWire()
    {
        var transport = new FakeTransport();
        var adapter = new FakeAdapter("simulator", "open-1", """{"credential":"c"}""");
        var sdk = new ChorusSdk(_ => transport);
        var session = await sdk.ConnectAsync(Configuration(adapter), adapter, CancellationToken.None);

        // The sync link (FIX-U3D-09) rides through: the position goes to the transport
        // verbatim and the transport's stream is the session's answer.
        var stream = await session.SubscribeAsync("room-1", 3, 12, 12, CancellationToken.None);
        Assert.Equal("room-1", transport.LastSubscribeRoom);
        Assert.Equal(3, transport.LastSubscribeEpoch);
        Assert.Equal(12, transport.LastSubscribeTick);
        Assert.Equal(12, transport.LastSubscribeVersion);
        Assert.Same(transport.LastStream, stream);

        // The intent link (FIX-U3D-10) rides through the same way: the intent is the
        // caller's own idempotency contract, passed verbatim.
        var intent = new ClientIntent
        {
            RoomId = "room-1", RoomEpoch = 3, CommandId = "cmd-1", PayloadJson = """{"action":"boost"}""",
        };
        var reply = await session.SendIntentAsync(intent, CancellationToken.None);
        Assert.Same(intent, transport.LastSentIntent);
        Assert.Equal("cmd-1", reply.CommandId);

        await session.DisconnectAsync();
        Assert.EndsWith("close", string.Join(",", transport.Sequence));
    }

    [Fact]
    public async Task AnIntentWithoutItsOwnIdentityIsRefusedBeforeTheWire()
    {
        var transport = new FakeTransport();
        var adapter = new FakeAdapter("simulator", "open-1", """{"credential":"c"}""");
        var sdk = new ChorusSdk(_ => transport);
        var session = await sdk.ConnectAsync(Configuration(adapter), adapter, CancellationToken.None);

        var noRoom = new ClientIntent { RoomId = "", RoomEpoch = 3, CommandId = "cmd-1", PayloadJson = "{}" };
        var roomFailure = await Assert.ThrowsAsync<ArgumentException>(
            () => session.SendIntentAsync(noRoom, CancellationToken.None));
        Assert.Contains("room", roomFailure.Message);

        var noEpoch = new ClientIntent { RoomId = "room-1", RoomEpoch = 0, CommandId = "cmd-1", PayloadJson = "{}" };
        var epochFailure = await Assert.ThrowsAsync<ArgumentException>(
            () => session.SendIntentAsync(noEpoch, CancellationToken.None));
        Assert.Contains("epoch", epochFailure.Message);

        // The idempotency key is the caller's own contract: the SDK never invents one.
        var noCommand = new ClientIntent { RoomId = "room-1", RoomEpoch = 3, CommandId = "", PayloadJson = "{}" };
        var commandFailure = await Assert.ThrowsAsync<ArgumentException>(
            () => session.SendIntentAsync(noCommand, CancellationToken.None));
        Assert.Contains("idempotency", commandFailure.Message);

        Assert.Null(transport.LastSentIntent);

        // The payload's json rule lives where the wire's payload lives: the edge
        // transport refuses an unreadable payload before anything else — a transport
        // that never connected still refuses it.
        var edge = new EdgeChorusTransport(Configuration(adapter));
        var badPayload = new ClientIntent
        {
            RoomId = "room-1", RoomEpoch = 3, CommandId = "cmd-1", PayloadJson = "not-json",
        };
        var payloadFailure = await Assert.ThrowsAsync<ArgumentException>(
            () => edge.SendIntentAsync(badPayload, CancellationToken.None));
        Assert.Contains("json", payloadFailure.Message);
    }

    [Fact]
    public void WsEndpointDerivationPinsTheSchemeAndPath()
    {
        Assert.Equal("wss://edge.example.com/ws", EdgeChorusTransport.WsEndpointFrom("https://edge.example.com"));
        Assert.Equal("wss://edge.example.com:8443/ws",
            EdgeChorusTransport.WsEndpointFrom("https://edge.example.com:8443"));
        Assert.Equal("wss://edge.example.com/ws", EdgeChorusTransport.WsEndpointFrom("wss://edge.example.com/ws"));
        Assert.Equal("ws://selfhost.example.com/ws", EdgeChorusTransport.WsEndpointFrom("http://selfhost.example.com"));
        Assert.Throws<ArgumentException>(() => EdgeChorusTransport.WsEndpointFrom("ftp://edge.example.com"));
    }

    [Fact]
    public async Task AUnitySdkLoginLinkRunsThroughARealGateway()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment);
        const string openId = "open-u3d07-1";
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(5));
        var adapter = new FakeAdapter("simulator", openId, credential);
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration,
            Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem)));
        var configuration = Configuration(adapter);
        configuration.Endpoint = $"https://localhost:{wssPort}";

        var session = await sdk.ConnectAsync(configuration, adapter, CancellationToken.None);

        // The login's identities as the server answered them, on the wire.
        Assert.False(string.IsNullOrEmpty(session.Login.StreamerId));
        Assert.False(string.IsNullOrEmpty(session.Login.SessionId));
        Assert.True(session.Login.AccessExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000);
        Assert.True(session.Login.ResumeRegistered);
        Assert.False(session.Login.Duplicate);
        // The gameplay binding started: the mount holds a real binding id.
        Assert.False(string.IsNullOrEmpty(session.BindingId));
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task ATamperedLaunchCredentialIsRefusedByTheEdgeOnTheWire()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment);
        const string openId = "open-u3d07-2";
        var valid = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(5));
        var tampered = valid[..^4] + "AAAA";
        var adapter = new FakeAdapter("simulator", openId, tampered);
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration,
            Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem)));
        var configuration = Configuration(adapter);
        configuration.Endpoint = $"https://localhost:{wssPort}";

        var failure = await Assert.ThrowsAsync<EdgeProtocolException>(
            () => sdk.ConnectAsync(configuration, adapter, CancellationToken.None));
        // The wire's error vocabulary is snake_case ("session_revoked").
        Assert.Equal("session_revoked", failure.Code);
    }

    private static SdkConfiguration Configuration(IPlatformAdapter adapter) => ConfigurationLike(adapter, 9);

    /// <summary>The configuration the match-link facts share with the login-link facts.</summary>
    internal static SdkConfiguration ConfigurationLike(IPlatformAdapter adapter, int gatewayPort) => new()
    {
        Platform = adapter.PlatformName,
        AppId = "app-1",
        Tenant = "tenant-a",
        GameId = "game-a",
        Endpoint = $"https://localhost:{gatewayPort}",
        Role = "streamer",
    };

    /// <summary>The platform adapter the match-link facts share with the login-link facts.</summary>
    internal static IPlatformAdapter FakeAdapterLike(string platform, string streamerId, string credential) =>
        new FakeAdapter(platform, streamerId, credential);

    /// <summary>The test certificates chain to a private root: the transport's
    /// certificate override validates against that root and its node authority, the
    /// same shape the transport contract tests pin.</summary>
    private static RemoteCertificateValidationCallback Trusts(string rootPem, string nodeAuthorityPem)
    {
        // The certs are NOT disposed: the closure must keep them alive for every
        // handshake this transport runs (a disposed cert breaks the chain build).
        var root = X509Certificate2.CreateFromPem(rootPem);
        var nodeAuthority = X509Certificate2.CreateFromPem(nodeAuthorityPem);
        return (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var candidate = new X509Certificate2(certificate);
            using var validator = new X509Chain();
            validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            validator.ChainPolicy.CustomTrustStore.Add(root);
            validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
            return validator.Build(candidate);
        };
    }

    private sealed class FakeAdapter : IPlatformAdapter
    {
        private readonly string _platform;
        private readonly string _streamerId;
        private readonly string _credential;

        public FakeAdapter(string platform, string streamerId, string credential)
        {
            _platform = platform;
            _streamerId = streamerId;
            _credential = credential;
        }

        public string PlatformName => _platform;

        public Task<PlatformMount> MountAsync(CancellationToken cancellationToken) => Task.FromResult(new PlatformMount
        {
            Platform = _platform,
            AppId = "app-1",
            StreamerId = _streamerId,
            UserId = string.Empty,
            Role = "streamer",
            SessionContextJson = _credential,
        });

        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private sealed class CancellingAdapter : IPlatformAdapter
    {
        public string PlatformName => "simulator";

        public async Task<PlatformMount> MountAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new PlatformMount();
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }

    /// <summary>The transport the orchestration tests control: it records the wire
    /// sequence and answers canned session facts.</summary>
    private sealed class FakeTransport : IChorusTransport
    {
        public List<string> Sequence { get; } = new();
        public LoginIntent? LastIntent { get; private set; }
        public string? LoginFailure { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            Sequence.Add("connect");
            return Task.CompletedTask;
        }

        public Task<LoginAnswer> LoginAsync(LoginIntent intent, CancellationToken cancellationToken)
        {
            Sequence.Add("login");
            LastIntent = intent;
            if (LoginFailure is not null)
                return Task.FromException<LoginAnswer>(new InvalidOperationException(LoginFailure));
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
            Sequence.Add("startGameplay");
            Assert.Equal("access-1", accessCredential);
            Assert.Equal("game-a", gameId);
            return Task.FromResult("binding-1");
        }

        public Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the login-link facts never join a match.");

        public Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the login-link facts never cancel a match.");

        public Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the login-link facts never await a room.");

        public string? LastSubscribeRoom { get; private set; }
        public long LastSubscribeEpoch { get; private set; }
        public long LastSubscribeTick { get; private set; }
        public long LastSubscribeVersion { get; private set; }
        public ISyncStream? LastStream { get; private set; }
        public ClientIntent? LastSentIntent { get; private set; }

        public Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken)
        {
            Sequence.Add("subscribe");
            LastSubscribeRoom = roomId;
            LastSubscribeEpoch = epoch;
            LastSubscribeTick = lastAppliedTick;
            LastSubscribeVersion = lastAppliedStateVersion;
            LastStream = new FakeSyncStream();
            return Task.FromResult<ISyncStream>(LastStream);
        }

        public Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken)
        {
            Sequence.Add("intent");
            LastSentIntent = intent;
            return Task.FromResult(new IntentReply
            {
                CommandId = intent.CommandId,
                RoomId = intent.RoomId,
                Sequence = 7,
            });
        }

        public Task CloseAsync()
        {
            Sequence.Add("close");
            return Task.CompletedTask;
        }
    }

    /// <summary>A stream that answers closed at once: the sync-link fact only pins that
    /// the transport's stream is what the session hands back.</summary>
    private sealed class FakeSyncStream : ISyncStream
    {
        public SyncFrame Current { get; } = new SyncFrame();

        public Task<bool> MoveNextAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
