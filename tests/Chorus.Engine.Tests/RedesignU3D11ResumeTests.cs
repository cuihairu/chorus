using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Chorus.UnitySdk;
using Chorus.UnitySdk.Edge;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The resume link (FIX-U3D-11): a dropped session rotates onto a fresh one — the
/// persisted pair (session id, resume secret) is the whole proof, no platform adapter
/// takes part — and the server's participation record re-enters the round: roomJoined,
/// then the head snapshot the declared baseline continues from. The orchestration is
/// pinned against a fake transport (sequence, rotation, refusals), and the real link
/// runs end to end against a real match cluster: a client drops mid-round, resumes,
/// rebaselines a fresh applier from the re-entry's snapshot and keeps playing.
/// </summary>
public sealed class RedesignU3D11ResumeTests
{
    [Fact]
    public async Task AResumeRotatesOntoAFreshSessionAndNamesTheSessionItResumes()
    {
        var transports = new List<FakeTransport>();
        var sdk = NewSdk(transports);
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", "open-1", """{"credential":"c"}""");
        var dropped = await sdk.ConnectAsync(
            RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, 9), adapter, CancellationToken.None);

        var resumed = await sdk.ResumeAsync(Configuration(9), dropped.Login.SessionId,
            dropped.ResumeCredential, "room-1", 12, CancellationToken.None);

        // The sequence, on a fresh wire, once.
        var transport = transports[1];
        Assert.Equal(new[] { "connect", "resume" }, transport.Sequence);
        // The intent named the session it resumes and presented its stored secret.
        var intent = transport.LastResume!;
        Assert.Equal(dropped.Login.SessionId, intent.PreviousSessionId);
        Assert.True(intent.ResumeCredential.SequenceEqual(dropped.ResumeCredential));
        Assert.StartsWith("resume-", intent.ResumeOperationId);
        // The room facts ride as diagnostics; the declared version seeds the re-baseline.
        Assert.Equal("room-1", intent.LastSeenRoomId);
        Assert.Equal(12, intent.LastAppliedStateVersion);
        // The rotation: a fresh secret for the fresh session, and the resumed session
        // carries it as the credential the NEXT resume presents.
        Assert.Equal(32, intent.NextResumeSecret.Length);
        Assert.False(intent.NextResumeSecret.SequenceEqual(dropped.ResumeCredential));
        Assert.Equal("session-2", resumed.Login.SessionId);
        Assert.True(resumed.Login.ResumeRegistered);
        Assert.True(intent.NextResumeSecret.SequenceEqual(resumed.ResumeCredential));
        // No gameplay start: the binding lives on the mount record the re-entry restores.
        Assert.Null(resumed.BindingId);
    }

    [Fact]
    public async Task EveryResumeGeneratesAFreshSecretAndOperationId()
    {
        var transports = new List<FakeTransport>();
        var sdk = NewSdk(transports);
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", "open-1", """{"credential":"c"}""");
        var dropped = await sdk.ConnectAsync(
            RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, 9), adapter, CancellationToken.None);

        var first = await sdk.ResumeAsync(Configuration(9), dropped.Login.SessionId,
            dropped.ResumeCredential, null, 0, CancellationToken.None);
        // The first resume rotated the secret; the second resume presents what the first
        // answered and rotates again.
        var second = await sdk.ResumeAsync(Configuration(9), first.Login.SessionId,
            first.ResumeCredential, null, 0, CancellationToken.None);

        var firstIntent = transports[1].LastResume!;
        var secondIntent = transports[2].LastResume!;
        Assert.False(firstIntent.NextResumeSecret.SequenceEqual(secondIntent.NextResumeSecret));
        Assert.NotEqual(firstIntent.ResumeOperationId, secondIntent.ResumeOperationId);
        Assert.True(secondIntent.ResumeCredential.SequenceEqual(first.ResumeCredential));
    }

    [Fact]
    public async Task AnIncompleteResumeIsRefusedBeforeTheWire()
    {
        var transports = new List<FakeTransport>();
        var sdk = NewSdk(transports);

        var noSession = await Assert.ThrowsAsync<ArgumentException>(() => sdk.ResumeAsync(
            Configuration(9), "", new byte[32], null, 0, CancellationToken.None));
        Assert.Contains("session", noSession.Message);

        var shortSecret = await Assert.ThrowsAsync<ArgumentException>(() => sdk.ResumeAsync(
            Configuration(9), "session-1", new byte[16], null, 0, CancellationToken.None));
        Assert.Contains("32-byte", shortSecret.Message);

        var negativeVersion = await Assert.ThrowsAsync<ArgumentException>(() => sdk.ResumeAsync(
            Configuration(9), "session-1", new byte[32], null, -1, CancellationToken.None));
        Assert.Contains("version", negativeVersion.Message);

        // The refusals never opened a wire: the validations precede the transport.
        Assert.Empty(transports);
    }

    [Fact]
    public async Task AFailedResumeClosesTheTransportBeforeTheFailureSurfaces()
    {
        var transport = new FakeTransport { ResumeFailure = "the resume secret was refused." };
        var sdk = new ChorusSdk(_ => transport);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sdk.ResumeAsync(
            Configuration(9), "session-1", new byte[32], null, 0, CancellationToken.None));
        Assert.Contains("the resume secret was refused.", failure.Message);
        // A half-open link never leaks: the wire closed even though the resume failed.
        Assert.Equal(new[] { "connect", "resume", "close" }, transport.Sequence);
    }

    [Fact]
    public async Task TheRejoinedRoomRidesThrough()
    {
        var sdk = NewSdk(new List<FakeTransport>());
        var resumed = await sdk.ResumeAsync(Configuration(9), "session-1", new byte[32],
            "room-1", 12, CancellationToken.None);

        // The participation record's room and the seat's facts; a resume consumes no
        // ticket, so the match's own entry facts stay empty.
        var rejoined = await resumed.AwaitRejoinedRoomAsync(CancellationToken.None);
        Assert.Equal("room-1", rejoined.RoomId);
        Assert.Equal(7, rejoined.RoomEpoch);
        Assert.Equal("home", rejoined.Participation);
        Assert.Equal("module-1", rejoined.ModuleVersion);
        Assert.Equal("cfg-1", rejoined.ConfigHash);
        Assert.Equal(string.Empty, rejoined.TicketId);
        Assert.Equal(string.Empty, rejoined.ReservationId);
    }

    [Fact]
    public async Task ADroppedClientResumesMidRoundAndTheFreshSnapshotRebaselinesItsApplier()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);

        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d11-a", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d11-b", certs);
        await first.JoinMatchAsync("standard", null, CancellationToken.None);
        await second.JoinMatchAsync("standard", null, CancellationToken.None);
        var room = await first.AwaitRoomAsync(CancellationToken.None);
        await second.AwaitRoomAsync(CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var stream = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);
        var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
        var caughtUp = new TaskCompletionSource<(long Tick, long Version)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = PumpUntilFirstDeltaAsync(stream, applier, caughtUp, budget.Token);
        var position = await caughtUp.Task.WaitAsync(budget.Token);

        // The drop: the wire dies mid-round; the persisted pair survives.
        var sessionId = first.Login.SessionId;
        var secret = first.ResumeCredential;
        await first.DisconnectAsync();
        await SwallowAsync(pump);

        // The resume: the pair is the whole proof, and it rotates.
        var resumed = await ResumeAsync(wssPort, certs, sessionId, secret, room.RoomId,
            position.Version, budget.Token);
        Assert.NotEqual(sessionId, resumed.Login.SessionId);
        Assert.True(resumed.Login.ResumeRegistered);
        Assert.Null(resumed.BindingId);
        Assert.False(resumed.ResumeCredential.SequenceEqual(secret));

        // The re-entry: the participation record's room, the seat in it.
        var rejoined = await resumed.AwaitRejoinedRoomAsync(budget.Token);
        Assert.Equal(room.RoomId, rejoined.RoomId);
        Assert.Equal(room.RoomEpoch, rejoined.RoomEpoch);
        Assert.False(string.IsNullOrEmpty(rejoined.Participation));
        Assert.False(string.IsNullOrEmpty(rejoined.ModuleVersion));

        // The catch-up: subscribe at the declared position, a fresh applier rebaselines
        // from the head snapshot the re-entry answers with, and the chain runs on.
        var again = await resumed.SubscribeAsync(rejoined.RoomId, rejoined.RoomEpoch,
            position.Tick, position.Version, budget.Token);
        var fresh = new SyncApplier(rejoined.RoomId, rejoined.RoomEpoch, DeltaContractMode.Full);
        var sawScoreEffect = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedPump = PumpAsync(again, fresh, sawScoreEffect, budget.Token);

        // The resumed link plays on: an intent into the re-entered room and its score
        // effect back in a committed delta — the seat never left the round.
        var reply = await resumed.SendIntentAsync(new ClientIntent
        {
            RoomId = rejoined.RoomId,
            RoomEpoch = rejoined.RoomEpoch,
            CommandId = "cmd-u3d11-1",
            PayloadJson = """{"action":"boost","level":2}""",
        }, budget.Token);
        Assert.Equal("cmd-u3d11-1", reply.CommandId);
        Assert.True(reply.Sequence > 0);
        var effect = await sawScoreEffect.Task.WaitAsync(budget.Token);
        Assert.Contains("\"faction\":\"red\"", effect);

        await second.DisconnectAsync();
        await resumed.DisconnectAsync();
        await SwallowAsync(resumedPump);
    }

    [Fact]
    public async Task AWrongResumeSecretIsRefusedVisiblyAndTheWireCloses()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        var session = await LoginAndStartAsync(wssPort, simulator, "open-u3d11-c", certs);

        var wrong = new byte[session.ResumeCredential.Length];
        session.ResumeCredential.CopyTo(wrong, 0);
        wrong[0] ^= 0xFF;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<EdgeProtocolException>(() => ResumeAsync(wssPort, certs,
            session.Login.SessionId, wrong, null, 0, budget.Token));

        await session.DisconnectAsync();
    }

    [Fact]
    public async Task AResumeWithNowhereToReturnAnnouncesNothingAndTheQuietBudgetDecides()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        var session = await LoginAndStartAsync(wssPort, simulator, "open-u3d11-d", certs);
        await session.DisconnectAsync();

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var resumed = await ResumeAsync(wssPort, certs, session.Login.SessionId,
            session.ResumeCredential, null, 0, budget.Token);
        // The wire announces no "nothing to re-enter": a resume whose mount holds no
        // round answers only loginReady, and the game's own budget decides.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resumed.AwaitRejoinedRoomAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
        await resumed.DisconnectAsync();
    }

    /// <summary>The game's pump loop: advance the stream, decide every frame, and name
    /// the first score effect a committed delta carries.</summary>
    private static async Task PumpAsync(ISyncStream stream, SyncApplier applier,
        TaskCompletionSource<string> watcher, CancellationToken cancellationToken)
    {
        try
        {
            while (await stream.MoveNextAsync(cancellationToken))
            {
                var decision = applier.Decide(stream.Current);
                if (decision.Kind != SyncDecisionKind.ApplyDelta) continue;
                var effect = stream.Current.Delta!.Effects.FirstOrDefault(entry =>
                    entry.PayloadJson.Contains("\"faction\":\"red\"", StringComparison.Ordinal));
                if (effect is not null) watcher.TrySetResult(effect.PayloadJson);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>The pump until the applier has chained its first delta: the position
    /// the game would persist before a drop — its own tick and version.</summary>
    private static async Task PumpUntilFirstDeltaAsync(ISyncStream stream, SyncApplier applier,
        TaskCompletionSource<(long Tick, long Version)> watcher, CancellationToken cancellationToken)
    {
        try
        {
            while (await stream.MoveNextAsync(cancellationToken))
            {
                var decision = applier.Decide(stream.Current);
                if (decision.Kind == SyncDecisionKind.ApplyDelta)
                {
                    watcher.TrySetResult((applier.Status.Tick, applier.Status.StateVersion));
                    return;
                }
            }
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

    /// <summary>The transport the resume-orchestration facts control: it answers a
    /// canned rotation and remembers the intent.</summary>
    private sealed class FakeTransport : IChorusTransport
    {
        public List<string> Sequence { get; } = new();
        public ResumeIntent? LastResume { get; private set; }
        public string? ResumeFailure { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            Sequence.Add("connect");
            return Task.CompletedTask;
        }

        public Task<LoginAnswer> LoginAsync(LoginIntent intent, CancellationToken cancellationToken) =>
            Task.FromResult(new LoginAnswer
            {
                StreamerId = "streamer-1",
                SessionId = "session-1",
                SessionVersion = 1,
                AccessCredential = "access-1",
                AccessExpiresAtUnixMs = 1,
                Duplicate = false,
                ResumeRegistered = true,
            });

        public Task<string> StartGameplayAsync(string accessCredential, string gameId, string operationId,
            CancellationToken cancellationToken) => Task.FromResult("binding-1");

        public Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the resume facts never join a match.");

        public Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the resume facts never cancel a match.");

        public Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the resume facts never await a room.");

        public Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the resume facts never subscribe.");

        public Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the resume facts never send intents.");

        public Task<LoginAnswer> ResumeAsync(ResumeIntent intent, CancellationToken cancellationToken)
        {
            Sequence.Add("resume");
            LastResume = intent;
            if (ResumeFailure is not null)
                return Task.FromException<LoginAnswer>(new InvalidOperationException(ResumeFailure));
            return Task.FromResult(new LoginAnswer
            {
                StreamerId = "streamer-1",
                SessionId = "session-2",
                SessionVersion = 2,
                AccessCredential = "access-2",
                AccessExpiresAtUnixMs = 1,
                Duplicate = false,
                ResumeRegistered = true,
            });
        }

        public Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new JoinedRoom
            {
                RoomId = "room-1",
                RoomEpoch = 7,
                Participation = "home",
                ModuleVersion = "module-1",
                ConfigHash = "cfg-1",
            });

        public Task CloseAsync()
        {
            Sequence.Add("close");
            return Task.CompletedTask;
        }
    }
}
