using System;
using System.Linq;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Chorus.UnitySdk;
using Chorus.UnitySdk.Edge;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The match and atomic room entry (FIX-U3D-08): the session's match leg — queue a
/// ticket, cancel it, and answer the atomic entry only when the committed room and
/// the seat's room facts are together — is pinned against a fake transport (intent
/// passthrough, ticket and entry mapping), and the real link runs end to end against
/// a real match cluster: two SDK clients queue into the same partition, both answer
/// the SAME committed room, and the atomic entry carries the seat's room facts.
/// </summary>
public sealed class RedesignU3D08MatchRoomLinkTests
{
    [Fact]
    public async Task JoinMatchPassesTheIntentAndTheTicketThrough()
    {
        var transport = new FakeTransport();
        var session = await Session(transport);

        var ticket = await session.JoinMatchAsync("standard", """{"mode":"ranked"}""", CancellationToken.None);

        var intent = transport.LastJoin!;
        Assert.StartsWith("join-", intent.OperationId);
        Assert.Equal("standard", intent.QueueType);
        Assert.Equal("""{"mode":"ranked"}""", intent.Options);
        Assert.Equal("ticket-1", ticket.TicketId);
        Assert.Equal("Queued", ticket.State);
    }

    [Fact]
    public async Task AJoinWithoutOptionsCarriesAnEmptyOptionsBlob()
    {
        var transport = new FakeTransport();
        var session = await Session(transport);
        await session.JoinMatchAsync("standard", null, CancellationToken.None);
        Assert.Equal(string.Empty, transport.LastJoin!.Options);
    }

    [Fact]
    public async Task CancelMatchPassesTheTicketAndAnswersTheTerminalState()
    {
        var transport = new FakeTransport();
        var session = await Session(transport);
        var cancelled = await session.CancelMatchAsync("ticket-9", CancellationToken.None);
        Assert.Equal("ticket-9", transport.LastCancelTicketId);
        Assert.StartsWith("cancel-", transport.LastCancelOperationId);
        Assert.Equal("Cancelled", cancelled.State);
    }

    [Fact]
    public async Task AwaitRoomAnswersTheAtomicEntry()
    {
        var transport = new FakeTransport();
        var session = await Session(transport);
        var room = await session.AwaitRoomAsync(CancellationToken.None);
        Assert.Equal("room-1", room.RoomId);
        Assert.Equal(7, room.RoomEpoch);
        Assert.Equal("home", room.Participation);
        Assert.Equal("module-1", room.ModuleVersion);
        Assert.Equal("cfg-1", room.ConfigHash);
        Assert.Equal("ticket-1", room.TicketId);
        Assert.Equal("res-1", room.ReservationId);
    }

    [Fact]
    public async Task TwoSdkClientsEnterTheSameCommittedRoomThroughARealMatchCluster()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);

        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d08-a", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d08-b", certs);

        var ticketA = await first.JoinMatchAsync("standard", null, CancellationToken.None);
        var ticketB = await second.JoinMatchAsync("standard", null, CancellationToken.None);
        Assert.Equal("Queued", ticketA.State);
        Assert.Equal("Queued", ticketB.State);
        Assert.False(string.IsNullOrEmpty(ticketA.TicketId));
        Assert.False(string.IsNullOrEmpty(ticketB.TicketId));

        var roomA = await first.AwaitRoomAsync(CancellationToken.None);
        var roomB = await second.AwaitRoomAsync(CancellationToken.None);
        // Atomic entry: the same committed room, same reservation, same ticket each.
        Assert.False(string.IsNullOrEmpty(roomA.RoomId));
        Assert.Equal(roomA.RoomId, roomB.RoomId);
        Assert.Equal(roomA.RoomEpoch, roomB.RoomEpoch);
        Assert.Equal(roomA.ReservationId, roomB.ReservationId);
        Assert.False(string.IsNullOrEmpty(roomA.ReservationId));
        Assert.Equal(ticketA.TicketId, roomA.TicketId);
        Assert.Equal(ticketB.TicketId, roomB.TicketId);
        // The seat's room facts: the side it plays and the module identity of frames.
        Assert.False(string.IsNullOrEmpty(roomA.Participation));
        Assert.False(string.IsNullOrEmpty(roomA.ModuleVersion));
        Assert.False(string.IsNullOrEmpty(roomA.ConfigHash));

        await first.DisconnectAsync();
        await second.DisconnectAsync();
    }

    [Fact]
    public async Task ACancelledTicketNeverEntersARoomAndTheConnectionStaysUsable()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        var session = await LoginAndStartAsync(wssPort, simulator, "open-u3d08-c", certs);

        var queued = await session.JoinMatchAsync("standard", null, CancellationToken.None);
        var cancelled = await session.CancelMatchAsync(queued.TicketId, CancellationToken.None);
        Assert.Equal("Cancelled", cancelled.State);
        Assert.Equal(queued.TicketId, cancelled.TicketId);
        await session.DisconnectAsync();
    }

    private static async Task<ChorusSession> LoginAndStartAsync(int wssPort,
        Chorus.Platform.DevelopmentPlatformSimulator simulator, string openId,
        RemoteCertificateValidationCallback certs)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(5));
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", openId, credential);
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration, certs));
        var configuration = RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, wssPort);
        return await sdk.ConnectAsync(configuration, adapter, CancellationToken.None);
    }

    private static async Task<ChorusSession> Session(FakeTransport transport)
    {
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", "open-1", """{"credential":"c"}""");
        var sdk = new ChorusSdk(_ => transport);
        return await sdk.ConnectAsync(RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, 9), adapter,
            CancellationToken.None);
    }

    private static RemoteCertificateValidationCallback Trusts(string rootPem, string nodeAuthorityPem)
    {
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

    /// <summary>The transport the orchestration facts control: it answers canned ticket
    /// and entry facts and remembers the intents.</summary>
    private sealed class FakeTransport : IChorusTransport
    {
        public JoinMatchIntent? LastJoin { get; private set; }
        public string? LastCancelOperationId { get; private set; }
        public string? LastCancelTicketId { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

        public Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken)
        {
            LastJoin = intent;
            return Task.FromResult(new MatchTicket { TicketId = "ticket-1", State = "Queued" });
        }

        public Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
            CancellationToken cancellationToken)
        {
            LastCancelOperationId = operationId;
            LastCancelTicketId = ticketId;
            return Task.FromResult(new MatchTicket { TicketId = ticketId, State = "Cancelled" });
        }

        public Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken) => Task.FromResult(new JoinedRoom
        {
            RoomId = "room-1",
            RoomEpoch = 7,
            Participation = "home",
            ModuleVersion = "module-1",
            ConfigHash = "cfg-1",
            TicketId = "ticket-1",
            ReservationId = "res-1",
        });

        public Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the match-link facts never subscribe.");

        public Task CloseAsync() => Task.CompletedTask;
    }
}
