using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The DataServer gRPC handlers under the production wiring — the same Kestrel mTLS
/// configuration, role resolution and store singletons Program.cs builds — hosted inside
/// the test process. The subprocess hosts the M-series suites start are invisible to the
/// coverage collector; these in-process hosts are not, so the handler guards, the tenant
/// fences and the exception-to-status mappings execute as real calls against a real
/// database instead of surviving only in the scripts of spawned hosts.
/// </summary>
public sealed class DataServerGrpcHandlerHostTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";
    private HandlerHost _host = null!;

    public async Task InitializeAsync() => _host = await HandlerHost.CreateAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task StartRound_AdmitsAndReplaysTheSameOperation()
    {
        var ready = await _host.ReadyAsync("@cluster", "control-server");
        Assert.True(ready.Ready);

        var mount = await _host.MountAsync("admit-open-1", "live-admit-1");
        var epoch = await _host.ClaimOwnership("room-admit-1");
        using var channel = _host.Channel("@cluster", "control-server", "admit-1");
        var client = new RoundStore.RoundStoreClient(channel);
        var request = RoundRequest("room-admit-1", epoch, "start-admit-1", new RoundParticipantProto
        {
            BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
        });
        var admitted = await client.StartRoundAsync(request);
        Assert.Equal("Preparing", admitted.Status);
        Assert.False(admitted.Duplicate);
        Assert.Equal(epoch, admitted.Epoch);

        var replay = await client.StartRoundAsync(request);
        Assert.Equal("Preparing", replay.Status);
        Assert.True(replay.Duplicate);
    }

    [Fact]
    public async Task StartRound_StaleEpochFailsPrecondition()
    {
        var epoch = await _host.ClaimOwnership("room-stale-1");
        using var channel = _host.Channel("@cluster", "control-server", "stale-1");
        var client = new RoundStore.RoundStoreClient(channel);
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.StartRoundAsync(RoundRequest("room-stale-1", epoch + 1, "start-stale-1")));
        Assert.Equal(StatusCode.FailedPrecondition, error.Status.StatusCode);
    }

    [Fact]
    public async Task StartRound_ForeignTenantCertificateIsPermissionDenied()
    {
        using var channel = _host.Channel("tenant-b", "control-server", "foreign-1");
        var client = new RoundStore.RoundStoreClient(channel);
        var request = RoundRequest("room-foreign-1", epoch: 1, "start-foreign-1");
        var error = await Assert.ThrowsAsync<RpcException>(async () => await client.StartRoundAsync(request));
        Assert.Equal(StatusCode.PermissionDenied, error.Status.StatusCode);
    }

    [Fact]
    public async Task StartRound_UnauthorizedRoleIsPermissionDenied()
    {
        using var channel = _host.Channel("@cluster", "node-agent", "intruder-1");
        var client = new RoundStore.RoundStoreClient(channel);
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.StartRoundAsync(RoundRequest("room-intruder-1", 1, "start-intruder-1")));
        Assert.Equal(StatusCode.PermissionDenied, error.Status.StatusCode);
    }

    [Fact]
    public async Task AbortRound_AbortsAndReplaysTheSameOperation()
    {
        var mount = await _host.MountAsync("abort-open-1", "live-abort-1");
        var epoch = await _host.ClaimOwnership("room-abort-1");
        using var channel = _host.Channel(Tenant, "control-server", "abort-1");
        var client = new RoundStore.RoundStoreClient(channel);
        await client.StartRoundAsync(RoundRequest("room-abort-1", epoch, "start-abort-1",
            new RoundParticipantProto
            {
                BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
            }));
        var abort = new AbortRoundRequest
        {
            GameId = "game-a", RoomId = "room-abort-1", OwnerInstanceId = "gs-1",
            Epoch = epoch, Reason = "handler-test", OperationId = "abort-abort-1"
        };
        var aborted = await client.AbortRoundAsync(abort);
        Assert.Equal("Aborted", aborted.Status);
        Assert.False(aborted.Duplicate);

        var replay = await client.AbortRoundAsync(abort);
        Assert.Equal("Aborted", replay.Status);
        Assert.True(replay.Duplicate);
    }

    [Fact]
    public async Task WithdrawParticipation_LeavesTheParticipationAndClearsTheRoomPointer()
    {
        var mount = await _host.MountAsync("withdraw-open-1", "live-withdraw-1");
        var epoch = await _host.ClaimOwnership("room-withdraw-1");
        using (var channel = _host.Channel("@cluster", "control-server", "withdraw-start"))
        {
            var rounds = new RoundStore.RoundStoreClient(channel);
            await rounds.StartRoundAsync(RoundRequest("room-withdraw-1", epoch, "start-withdraw-1",
                new RoundParticipantProto
                {
                    BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
                }));
        }
        Assert.Equal("room-withdraw-1", await _host.BindingRoomAsync(mount.BindingId));

        using var gameChannel = _host.Channel("@cluster", "game-server", "withdraw-1");
        var input = new RoundInput.RoundInputClient(gameChannel);
        var withdrawn = await input.WithdrawParticipationAsync(new WithdrawParticipationRequest
        {
            TenantId = Tenant, SessionId = mount.SessionId, RoomId = "room-withdraw-1",
            GameId = "game-a", OperationId = "withdraw-op-1"
        });
        Assert.Equal("room-withdraw-1", withdrawn.RoomId);
        Assert.Equal("Left", withdrawn.State);
        Assert.False(withdrawn.Duplicate);
        Assert.Null(await _host.BindingRoomAsync(mount.BindingId));
        Assert.Equal("Left", await _host.ParticipationStateAsync("room-withdraw-1", mount.StreamerId));

        var replay = await input.WithdrawParticipationAsync(new WithdrawParticipationRequest
        {
            TenantId = Tenant, SessionId = mount.SessionId, RoomId = "room-withdraw-1",
            GameId = "game-a", OperationId = "withdraw-op-1"
        });
        Assert.Equal("Left", replay.State);
        Assert.True(replay.Duplicate);
    }

    [Fact]
    public async Task WithdrawParticipation_SessionScopedCallerNamesItsOwnTenant()
    {
        var mount = await _host.MountAsync("withdraw-open-2", "live-withdraw-2");
        var epoch = await _host.ClaimOwnership("room-withdraw-2");
        using (var channel = _host.Channel("@cluster", "control-server", "withdraw-start-2"))
        {
            await new RoundStore.RoundStoreClient(channel).StartRoundAsync(RoundRequest(
                "room-withdraw-2", epoch, "start-withdraw-2", new RoundParticipantProto
                {
                    BindingId = mount.BindingId, ParticipantId = mount.StreamerId, Role = "Host"
                }));
        }
        using var channel2 = _host.Channel(Tenant, "streamer-server", "withdraw-2");
        var withdrawn = await new RoundInput.RoundInputClient(channel2).WithdrawParticipationAsync(
            new WithdrawParticipationRequest
            {
                SessionId = mount.SessionId, RoomId = "room-withdraw-2", GameId = "game-a",
                OperationId = "withdraw-op-2"
            });
        Assert.Equal("Left", withdrawn.State);
    }

    [Fact]
    public async Task WithdrawParticipation_UnknownSessionIsPermissionDenied()
    {
        using var channel = _host.Channel("@cluster", "game-server", "withdraw-ghost");
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await new RoundInput.RoundInputClient(channel).WithdrawParticipationAsync(
                new WithdrawParticipationRequest
                {
                    TenantId = Tenant, SessionId = "no-such-session", RoomId = "room-ghost",
                    GameId = "game-a", OperationId = "withdraw-ghost-1"
                }));
        Assert.Equal(StatusCode.PermissionDenied, error.Status.StatusCode);
    }

    [Fact]
    public async Task WithdrawParticipation_NonParticipantFailsPrecondition()
    {
        var bystander = await _host.MountAsync("withdraw-open-3", "live-withdraw-3");
        var occupant = await _host.MountAsync("occupant-open-3", "live-occupant-3");
        var epoch = await _host.ClaimOwnership("room-withdraw-3");
        using (var channel = _host.Channel("@cluster", "control-server", "withdraw-start-3"))
        {
            // The room runs with the occupant in it; the bystander only holds a session.
            await new RoundStore.RoundStoreClient(channel).StartRoundAsync(RoundRequest(
                "room-withdraw-3", epoch, "start-withdraw-3", new RoundParticipantProto
                {
                    BindingId = occupant.BindingId, ParticipantId = occupant.StreamerId, Role = "Host"
                }));
        }
        using var gameChannel = _host.Channel("@cluster", "game-server", "withdraw-3");
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await new RoundInput.RoundInputClient(gameChannel).WithdrawParticipationAsync(
                new WithdrawParticipationRequest
                {
                    TenantId = Tenant, SessionId = bystander.SessionId, RoomId = "room-withdraw-3",
                    GameId = "game-a", OperationId = "withdraw-op-3"
                }));
        Assert.Equal(StatusCode.FailedPrecondition, error.Status.StatusCode);
    }

    [Fact]
    public async Task WithdrawParticipation_UnauthorizedRoleIsPermissionDenied()
    {
        using var channel = _host.Channel("@cluster", "platform-gateway", "withdraw-intruder");
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await new RoundInput.RoundInputClient(channel).WithdrawParticipationAsync(
                new WithdrawParticipationRequest
                {
                    TenantId = Tenant, SessionId = "s", RoomId = "r", GameId = "game-a",
                    OperationId = "op"
                }));
        Assert.Equal(StatusCode.PermissionDenied, error.Status.StatusCode);
    }

    [Fact]
    public async Task WithdrawParticipation_ForeignTenantRequestIsPermissionDenied()
    {
        using var channel = _host.Channel(Tenant, "game-server", "withdraw-tenant");
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await new RoundInput.RoundInputClient(channel).WithdrawParticipationAsync(
                new WithdrawParticipationRequest
                {
                    TenantId = "tenant-b", SessionId = "s", RoomId = "r", GameId = "game-a",
                    OperationId = "op"
                }));
        Assert.Equal(StatusCode.PermissionDenied, error.Status.StatusCode);
    }

    /// <summary>A round needs at least one participant and the admission fences on the
    /// tenant the ownership claim was seeded under, so every request names both.</summary>
    private static StartRoundRequest RoundRequest(string roomId, long epoch, string operationId,
        RoundParticipantProto? participant = null)
    {
        var request = new StartRoundRequest
        {
            RoomId = roomId, OwnerInstanceId = "gs-1", Epoch = epoch, GameId = "game-a",
            ModuleVersion = "1.0.0", ConfigHash = "cfg-handler-host", PackageHash = "pkg-1",
            OperationId = operationId, TenantId = Tenant
        };
        request.Participants.Add(participant ?? new RoundParticipantProto
        {
            BindingId = "binding-" + roomId, ParticipantId = "streamer-" + roomId, Role = "Host"
        });
        return request;
    }

    /// <summary>
    /// One in-process DataServer: its own database, its own certificate chain, the exact
    /// Kestrel mTLS + store wiring Program.cs runs. Nothing here spawns a process.
    /// </summary>
    internal sealed class HandlerHost : IAsyncDisposable
    {
        private static readonly string[] NodeRoles =
        [
            "node-agent", "control-server", "data-server", "streamer-server",
            "game-server", "platform-gateway", "streamer-gateway"
        ];
        private readonly string _databaseName = "chorus_handler_host_" + Guid.NewGuid().ToString("N");
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "chorus-handler-host-" + Guid.NewGuid().ToString("N"));
        private readonly TrustedProcessCertificates _trusted = new();
        private NpgsqlConnectionStringBuilder _admin = null!;
        private X509Certificate2 _root = null!;
        private X509Certificate2 _nodeAuthority = null!;
        private WebApplication _app = null!;
        private X509Certificate2 _serverCertificate = null!;

        public int Port { get; private set; }
        public string DatabaseConnectionString { get; private set; } = "";

        public static async Task<HandlerHost> CreateAsync()
        {
            var host = new HandlerHost();
            await host.InitializeAsync();
            return host;
        }

        private async Task InitializeAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
            _admin = new NpgsqlConnectionStringBuilder(source) { Pooling = false };
            await using (var admin = new NpgsqlConnection(_admin.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
            DatabaseConnectionString = new NpgsqlConnectionStringBuilder(_admin.ConnectionString)
            {
                Database = _databaseName
            }.ConnectionString;
            await using (var connection = new NpgsqlConnection(DatabaseConnectionString))
            {
                await connection.OpenAsync();
                await RedesignSchemaMigrator.ApplyAsync(connection);
            }

            Directory.CreateDirectory(_directory);
            _root = ProcessCertificateIssuer.CreateRoot("cluster-a", DateTimeOffset.UtcNow.AddDays(10));
            _nodeAuthority = ProcessCertificateIssuer.CreateNodeAuthority(_root,
                new NodeCertificateGrant("cluster-a", "node-a", NodeRoles,
                    ["@cluster", Tenant, "tenant-b"]),
                DateTimeOffset.UtcNow.AddDays(5));
            _serverCertificate = Certificate("@cluster", "data-server", "handler-host", "localhost");

            Port = FreePort();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = _serverCertificate;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (certificate, _, _) =>
                        _trusted.ValidateAndRemember(certificate, _root, "cluster-a", [_nodeAuthority]);
                });
            }));
            // The DataServer wiring: every store is a bare singleton over one connection
            // string (Program.cs 38-79, without the environment seams the handler facts
            // do not exercise).
            builder.Services.AddSingleton(_trusted);
            builder.Services.AddSingleton(new RedesignPlayerStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignIdentityStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignBindingStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignInputEventStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignPlatformTaskStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignMatchTicketStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignMatchReservationStore(DatabaseConnectionString,
                maxActiveReservations: null));
            builder.Services.AddSingleton(new RedesignRoomStore(DatabaseConnectionString,
                maxActiveRooms: null));
            builder.Services.AddSingleton(new RedesignRoundInputStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignFrameStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignRecoveryStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignSettlementStore(DatabaseConnectionString, null));
            builder.Services.AddSingleton(new RedesignRetentionStore(DatabaseConnectionString));
            builder.Services.AddSingleton(new RedesignLeaderboardStore(DatabaseConnectionString));
            builder.Services.AddGrpc();
            var app = builder.Build();
            app.MapGrpcService<DataGrpcService>();
            app.MapGrpcService<StreamerIdentityGrpcService>();
            app.MapGrpcService<PlatformIngestGrpcService>();
            app.MapGrpcService<PlatformTaskGrpcService>();
            app.MapGrpcService<MatchQueueGrpcService>();
            app.MapGrpcService<MatchGrpcService>();
            app.MapGrpcService<RoundGrpcService>();
            app.MapGrpcService<RoundInputGrpcService>();
            app.MapGrpcService<FrameGrpcService>();
            app.MapGrpcService<SettlementGrpcService>();
            app.MapGrpcService<LeaderboardGrpcService>();
            app.MapGrpcService<RetentionGrpcService>();
            _app = app;
            _app.Start();
        }

        /// <summary>Issues a process certificate as the node authority would.</summary>
        public X509Certificate2 Certificate(string tenant, string role, string instance,
            string? serverDnsName = null) =>
            ProcessCertificateIssuer.CreateProcess(_nodeAuthority,
                new ProcessIdentity("cluster-a", tenant, "node-a", role, instance),
                DateTimeOffset.UtcNow.AddDays(1), serverDnsName);

        public Grpc.Net.Client.GrpcChannel Channel(string tenant, string role, string instance)
        {
            var certificate = Certificate(tenant, role, instance);
            return ProcessGrpcClientFactory.Create(new Uri($"https://localhost:{Port}"),
                certificate, _root, _nodeAuthority, "cluster-a", "data-server");
        }

        public async Task<ReadyReply> ReadyAsync(string tenant, string role)
        {
            using var channel = Channel(tenant, role, "ready-probe");
            return await new RoundStore.RoundStoreClient(channel).ReadyAsync(new Empty());
        }

        /// <summary>Claims the room ownership the data-plane admission fences on, returning
        /// the epoch the StartRound call must carry.</summary>
        public async Task<long> ClaimOwnership(string roomId)
        {
            var control = new RedesignControlStore(DatabaseConnectionString);
            var claim = await control.ClaimOwnershipAsync(Tenant, "game-a", "room", roomId, "gs-1", 60);
            return claim.Epoch;
        }

        /// <summary>Mounts a streamer the way the gateways do: identity, live session,
        /// login session, gameplay binding.</summary>
        public async Task<Mounted> MountAsync(string openId, string liveKey)
        {
            var identity = new RedesignIdentityStore(DatabaseConnectionString);
            var bindings = new RedesignBindingStore(DatabaseConnectionString);
            var streamer = await identity.ResolveIdentityAsync(Tenant, "simulator", "app-1", openId);
            var live = await bindings.OpenLiveSessionAsync(Tenant, streamer.StreamerId, "simulator",
                "app-1", "platform-room", liveKey);
            var session = await identity.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
                RandomNumberGenerator.GetBytes(32));
            var mount = await bindings.StartGameplayAsync(Tenant, live, session.SessionId,
                "game-a", "mount-" + openId);
            return new Mounted(mount.BindingId, streamer.StreamerId, session.SessionId);
        }

        public async Task<string?> BindingRoomAsync(string bindingId)
        {
            await using var connection = new NpgsqlConnection(DatabaseConnectionString);
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand(
                "SELECT current_room_id FROM live.binding WHERE binding_id=@binding", connection);
            query.Parameters.AddWithValue("binding", bindingId);
            return await query.ExecuteScalarAsync() as string;
        }

        public async Task<string?> ParticipationStateAsync(string roomId, string streamerId)
        {
            await using var connection = new NpgsqlConnection(DatabaseConnectionString);
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand(
                "SELECT state FROM game.participation WHERE room_id=@room AND participant_id=@streamer",
                connection);
            query.Parameters.AddWithValue("room", roomId);
            query.Parameters.AddWithValue("streamer", streamerId);
            return await query.ExecuteScalarAsync() as string;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            await using (var admin = new NpgsqlConnection(_admin.ConnectionString))
            {
                await admin.OpenAsync();
                await using var drop = new NpgsqlCommand(
                    $"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
            try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        }
    }

    internal sealed record Mounted(string BindingId, string StreamerId, string SessionId);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>Tiny local helper so the request builder stays one expression.</summary>
file static class RequestTapExtensions
{
    public static T Tap<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
