using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The match queue face driven as plain method calls. Enqueue walks the mount's live chain —
/// session, streamer state, binding, participation — under the unified lock order, so a face
/// test seeds the real rows rather than stubs; one active ticket per mount is the partial
/// unique index, and only the coordinator reads windows or sweeps.
/// </summary>
public sealed class RedesignMatchQueueGrpcFaceTests
{
    private const string Instance = "ss-1";
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";
    private const string Binding = "binding-1";
    private const string Session = "session-1";
    private const string Partition = "tenant-a:game-a:1:eu:simulator:ranked";

    [Fact]
    public async Task ReadyAnswersForTheQueueCallers()
    {
        await using var rig = await QueueRig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("control-server", "cs-1"));
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheQueuePlane()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("data-server", "data-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task EnqueueTicketQueuesTheMount()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        var reply = await rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance));
        Assert.True(reply.Found);
        Assert.False(reply.Duplicate);
        var ticket = reply.Ticket;
        Assert.NotEmpty(ticket.TicketId);
        Assert.Equal(Binding, ticket.BindingId);
        Assert.Equal("streamer-1", ticket.StreamerId);
        Assert.Equal("Queued", ticket.State);
        Assert.Equal(1, ticket.Version);
        Assert.Equal(Partition, ticket.PartitionKey);
        Assert.Equal(0, ticket.ExpiresUnixMs);
        Assert.Equal("", ticket.ReservationId);
        Assert.Equal(Tenant, ticket.TenantId);
        Assert.Equal(Game, ticket.GameId);
    }

    [Fact]
    public async Task EnqueueTicketAnswersDuplicateForAnExactReplay()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.EnqueueTicket(Enqueue(), streamer);
        var reply = await rig.Service.EnqueueTicket(Enqueue(), streamer);
        Assert.True(reply.Duplicate);
        Assert.Equal("Queued", reply.Ticket.State);
    }

    [Fact]
    public async Task EnqueueTicketRefusesAChangedEnqueueUnderTheSameOperationId()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.EnqueueTicket(Enqueue(), streamer);
        await rig.Rejects(() => rig.Service.EnqueueTicket(
                Enqueue(partitionKey: "tenant-a:game-a:1:eu:simulator:casual"), streamer),
            StatusCode.InvalidArgument, "The operation was retried with a different request.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesARevokedSession()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.ExecuteAsync("UPDATE auth.session SET revoked_at = clock_timestamp() WHERE session_id='session-1'");
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance)),
            StatusCode.PermissionDenied, "Session is revoked.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesAReplacedSession()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.ExecuteAsync(
            "UPDATE auth.streamer_state SET current_session_id='session-2' WHERE streamer_id='streamer-1'");
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance)),
            StatusCode.PermissionDenied, "Session is revoked.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesABannedStreamer()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.ExecuteAsync(
            "UPDATE auth.streamer_state SET ban_state='Suspended' WHERE streamer_id='streamer-1'");
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance)),
            StatusCode.PermissionDenied, "Session is revoked.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesAnExpiredSession()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.ExecuteAsync(
            "UPDATE auth.session SET expires_at = clock_timestamp() - interval '1 minute' WHERE session_id='session-1'");
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance)),
            StatusCode.PermissionDenied, "Session is revoked.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesAMissingBinding()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(bindingId: "binding-other"),
                rig.Context("streamer-server", Instance)),
            StatusCode.FailedPrecondition, "The binding does not exist.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesABindingOfAnotherScope()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding, tenant: "tenant-b", game: "game-b");
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance)),
            StatusCode.FailedPrecondition, "The binding belongs to another tenant or game.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesAnInactiveBinding()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding, state: "Closing");
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance)),
            StatusCode.FailedPrecondition, "The binding is no longer active.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesABindingOwnedByAnotherStreamer()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.ExecuteAsync("""
            INSERT INTO auth.streamer_state(streamer_id, tenant_id, current_session_id, ban_state)
                VALUES ('streamer-2', 'tenant-a', 'session-1', 'Allowed');
            UPDATE live.session SET streamer_id='streamer-2'
                WHERE live_session_id IN (SELECT live_session_id FROM live.binding WHERE binding_id='binding-1')
            """);
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance)),
            StatusCode.FailedPrecondition, "The binding belongs to another streamer.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesAMountAlreadyInARound()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.ExecuteAsync("""
            INSERT INTO game.room(room_id, tenant_id, game_id, status, epoch, package_hash,
                module_version, config_hash)
                VALUES ('room-1', 'tenant-a', 'game-a', 'Running', 1, 'pkg', 'v1', 'cfg');
            INSERT INTO game.participation(room_id, participant_id, binding_id, role, state)
                VALUES ('room-1', 'p-1', 'binding-1', 'player', 'Active')
            """);
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("streamer-server", Instance)),
            StatusCode.FailedPrecondition, "The mount already participates in a round.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesASecondActiveTicket()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.EnqueueTicket(Enqueue(), streamer);
        // One active ticket per mount is the partial unique index: a second enqueue under a
        // fresh operation id is a visible refusal, not an overwrite.
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(operationId: "op-queue-9"), streamer),
            StatusCode.FailedPrecondition, "The mount already holds an active ticket.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesABlankTenant()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(tenantId: ""),
                rig.Context("streamer-server", Instance)),
            StatusCode.InvalidArgument, "Tenant is required.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesATenantScopedCallerActingForAnotherTenant()
    {
        await using var rig = await QueueRig.CreateAsync();
        var tenant = rig.Certificates.Context(rig.Certificates.Issue("tenant-a", "streamer-server", Instance));
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(tenantId: "tenant-b"), tenant),
            StatusCode.PermissionDenied, "Tenant is not authorized.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesACoordinatorReader()
    {
        await using var rig = await QueueRig.CreateAsync();
        // The game server reads tickets but never enqueues: the queue is written by the
        // mount's own plane, not by the round it may later join.
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(), rig.Context("game-server", "gs-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task EnqueueTicketRefusesABlankSessionId()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.EnqueueTicket(Enqueue(sessionId: ""),
                rig.Context("streamer-server", Instance)),
            StatusCode.InvalidArgument, "Value is required. (Parameter 'sessionId')");
    }

    [Fact]
    public async Task CancelTicketCancelsTheQueuedTicket()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.EnqueueTicket(Enqueue(), streamer);
        var reply = await rig.Service.CancelTicket(Cancel(), streamer);
        Assert.True(reply.Found);
        Assert.False(reply.Duplicate);
        Assert.Equal("Cancelled", reply.Ticket.State);
        Assert.Equal(2, reply.Ticket.Version);
    }

    [Fact]
    public async Task CancelTicketAnswersDuplicateForAnExactReplay()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.EnqueueTicket(Enqueue(), streamer);
        await rig.Service.CancelTicket(Cancel(), streamer);
        var reply = await rig.Service.CancelTicket(Cancel(), streamer);
        Assert.True(reply.Duplicate);
        Assert.Equal("Cancelled", reply.Ticket.State);
    }

    [Fact]
    public async Task CancelTicketRefusesAMountWithoutATicket()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.Rejects(() => rig.Service.CancelTicket(Cancel(), rig.Context("streamer-server", Instance)),
            StatusCode.FailedPrecondition, "The mount holds no active ticket to cancel.");
    }

    [Fact]
    public async Task CancelTicketRefusesATicketHeldByAReservation()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        await rig.SeedTicketAsync(Binding, "t-res", "Reserved", reservationId: "res-1");
        await rig.Rejects(() => rig.Service.CancelTicket(Cancel(), rig.Context("streamer-server", Instance)),
            StatusCode.FailedPrecondition,
            "A ticket in state Reserved is held by a reservation and cannot be cancelled.");
    }

    [Fact]
    public async Task CancelTicketRefusesAMissingBinding()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.CancelTicket(Cancel(bindingId: "binding-other"),
                rig.Context("streamer-server", Instance)),
            StatusCode.FailedPrecondition, "The binding does not exist.");
    }

    [Fact]
    public async Task GetTicketAnswersTheLatestStateForTheGameServer()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.EnqueueTicket(Enqueue(), streamer);
        await rig.Service.CancelTicket(Cancel(), streamer);
        var reply = await rig.Service.GetTicket(
            new GetTicketRequest { TenantId = Tenant, GameId = Game, BindingId = Binding },
            rig.Context("game-server", "gs-1"));
        Assert.True(reply.Found);
        Assert.Equal("Cancelled", reply.Ticket.State);
        Assert.Equal(2, reply.Ticket.Version);
    }

    [Fact]
    public async Task GetTicketAnswersNotFoundWithoutATicket()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync(Binding);
        var reply = await rig.Service.GetTicket(
            new GetTicketRequest { TenantId = Tenant, GameId = Game, BindingId = Binding },
            rig.Context("game-server", "gs-1"));
        Assert.False(reply.Found);
        Assert.Null(reply.Ticket);
    }

    [Fact]
    public async Task GetTicketRefusesABlankBinding()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.GetTicket(
                new GetTicketRequest { TenantId = Tenant, GameId = Game, BindingId = "" },
                rig.Context("game-server", "gs-1")),
            StatusCode.InvalidArgument, "Value is required. (Parameter 'bindingId')");
    }

    [Fact]
    public async Task ListQueuedAnswersTheLiveWindowOfOnePartition()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync("binding-1");
        await rig.SeedMountAsync("binding-2");
        await rig.SeedMountAsync("binding-3");
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.EnqueueTicket(Enqueue(bindingId: "binding-1"), streamer);
        await rig.Service.EnqueueTicket(Enqueue(bindingId: "binding-2"), streamer);
        // A ticket past its own expiry is not a candidate: the window excludes it and the
        // sweep retires it.
        await rig.SeedTicketAsync("binding-3", "t-exp", "Queued", expired: true);
        var reply = await rig.Service.ListQueued(new ListQueuedRequest { PartitionKey = Partition, Max = 10 },
            rig.Context("control-server", "cs-1"));
        Assert.Equal(2, reply.Tickets.Count);
        Assert.All(reply.Tickets, ticket => Assert.Equal("Queued", ticket.State));
    }

    [Fact]
    public async Task ListQueuedRefusesAnOutOfWindowMax()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ListQueued(new ListQueuedRequest { PartitionKey = Partition, Max = 0 },
                rig.Context("control-server", "cs-1")),
            StatusCode.InvalidArgument, "The candidate window is 1..500 tickets. (Parameter 'max')");
    }

    [Fact]
    public async Task ListQueuedRefusesARoleOutsideTheCoordinator()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ListQueued(new ListQueuedRequest { PartitionKey = Partition, Max = 10 },
                rig.Context("streamer-server", Instance)),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task ExpireQueuedRetiresOnlyPassedTickets()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync("binding-1");
        await rig.SeedMountAsync("binding-2");
        await rig.SeedTicketAsync("binding-1", "t-exp", "Queued", expired: true);
        await rig.Service.EnqueueTicket(Enqueue(bindingId: "binding-2"), rig.Context("streamer-server", Instance));
        var reply = await rig.Service.ExpireQueued(new ExpireQueuedRequest { Max = 100 },
            rig.Context("control-server", "cs-1"));
        Assert.Equal(1, reply.Expired);
        // The sweep is idempotent by predicate: a second pass finds nothing left to retire.
        var again = await rig.Service.ExpireQueued(new ExpireQueuedRequest { Max = 100 },
            rig.Context("control-server", "cs-1"));
        Assert.Equal(0, again.Expired);
    }

    [Fact]
    public async Task ExpireQueuedRefusesAnOutOfWindowMax()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ExpireQueued(new ExpireQueuedRequest { Max = 501 },
                rig.Context("control-server", "cs-1")),
            StatusCode.InvalidArgument, "The sweep window is 1..500 tickets. (Parameter 'max')");
    }

    [Fact]
    public async Task ListPartitionsAnswersThePartitionsWithLiveCandidates()
    {
        await using var rig = await QueueRig.CreateAsync();
        await rig.SeedMountAsync("binding-1");
        await rig.SeedMountAsync("binding-2");
        var streamer = rig.Context("streamer-server", Instance);
        await rig.Service.EnqueueTicket(Enqueue(bindingId: "binding-1", partitionKey: "p-a"), streamer);
        await rig.Service.EnqueueTicket(Enqueue(bindingId: "binding-2", partitionKey: "p-b"), streamer);
        await rig.Service.CancelTicket(Cancel(bindingId: "binding-2"), streamer);
        // A partition without queued tickets holds no term, so the cancelled one drops out.
        var reply = await rig.Service.ListPartitions(new Empty(), rig.Context("control-server", "cs-1"));
        Assert.Equal(["p-a"], reply.PartitionKeys);
    }

    private static EnqueueTicketRequest Enqueue(
        string tenantId = Tenant, string gameId = Game, string bindingId = Binding,
        string sessionId = Session, string partitionKey = Partition,
        string operationId = "op-queue-1", long expiresUnixMs = 0) => new()
    {
        TenantId = tenantId, GameId = gameId, BindingId = bindingId, SessionId = sessionId,
        PartitionKey = partitionKey, OperationId = operationId, ExpiresUnixMs = expiresUnixMs
    };

    private static CancelTicketRequest Cancel(string bindingId = Binding,
        string operationId = "op-queue-2") => new()
    { TenantId = Tenant, GameId = Game, BindingId = bindingId, OperationId = operationId };

    private sealed record QueueRig(MatchQueueGrpcService Service,
        TestProcessCertificates Certificates, TemporaryDatabase Database) : IAsyncDisposable
    {
        public static async Task<QueueRig> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            var certificates = new TestProcessCertificates();
            var store = new RedesignMatchTicketStore(database.ConnectionString);
            return new QueueRig(new MatchQueueGrpcService(store, certificates.Trusted),
                certificates, database);
        }

        /// <summary>Mounts the live chain the enqueue walks under the unified lock order:
        /// session, streamer state, live session, then the binding itself. The shared rows
        /// are inserted once per database; each call adds its own binding.</summary>
        public async Task SeedMountAsync(string bindingId, string tenant = Tenant,
            string game = Game, string state = "Ready")
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            // Each binding carries its own live session: the active-binding index allows one
            // live binding per live session, so multi-mount facts chain distinct sessions.
            await using var seed = new NpgsqlCommand($"""
                INSERT INTO auth.streamer_state(streamer_id, tenant_id, current_session_id, ban_state)
                    VALUES ('streamer-1', 'tenant-a', 'session-1', 'Allowed')
                    ON CONFLICT (streamer_id) DO NOTHING;
                INSERT INTO auth.session(session_id, streamer_id, session_version, gateway_instance_id,
                    connection_id, resume_hash, expires_at)
                    VALUES ('session-1', 'streamer-1', 1, 'gw-1', 'conn-1', decode('00', 'hex'),
                        clock_timestamp() + interval '10 minutes')
                    ON CONFLICT (session_id) DO NOTHING;
                INSERT INTO live.session(live_session_id, tenant_id, streamer_id, platform, app_id,
                    platform_room_key, canonical_live_session_key, started_at)
                    VALUES (@live, 'tenant-a', 'streamer-1', 'simulator', 'app-1', @live,
                        'simulator:' || @binding, clock_timestamp())
                    ON CONFLICT (live_session_id) DO NOTHING;
                INSERT INTO live.binding(binding_id, live_session_id, tenant_id, game_id, generation,
                    state, current_room_id, round_generation)
                    VALUES (@binding, @live, @tenant, @game, 1, @state, NULL, 1)
                """, connection);
            seed.Parameters.AddWithValue("binding", bindingId);
            seed.Parameters.AddWithValue("live", "live-" + bindingId);
            seed.Parameters.AddWithValue("tenant", tenant);
            seed.Parameters.AddWithValue("game", game);
            seed.Parameters.AddWithValue("state", state);
            await seed.ExecuteNonQueryAsync();
        }

        /// <summary>Plants a ticket row directly — the reserved-held and expired-sweep facts
        /// need states the enqueue path itself never produces.</summary>
        public async Task SeedTicketAsync(string bindingId, string ticketId, string state,
            bool expired = false, string? reservationId = null)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            // A reservation-held ticket references the reservation row the hold names.
            await using (var holder = new NpgsqlCommand("""
                INSERT INTO match.reservation(reservation_id, room_id, partition_key,
                    coordinator_term, expires_at, status, tenant_id, game_id)
                    VALUES ('res-1', 'room-held-1', @partition, 1,
                        clock_timestamp() + interval '5 minutes', 'Held', @tenant, @game)
                    ON CONFLICT (reservation_id) DO NOTHING
                """, connection))
            {
                holder.Parameters.AddWithValue("partition", Partition);
                holder.Parameters.AddWithValue("tenant", Tenant);
                holder.Parameters.AddWithValue("game", Game);
                await holder.ExecuteNonQueryAsync();
            }
            await using var seed = new NpgsqlCommand("""
                INSERT INTO match.ticket(ticket_id, tenant_id, game_id, binding_id, streamer_id,
                    state, version, partition_key, expires_at, reservation_id)
                VALUES (@ticket, @tenant, @game, @binding, 'streamer-1', @state, 1, @partition,
                    CASE WHEN @expired THEN clock_timestamp() - interval '1 minute' END,
                    @reservation)
                """, connection);
            seed.Parameters.AddWithValue("ticket", ticketId);
            seed.Parameters.AddWithValue("tenant", Tenant);
            seed.Parameters.AddWithValue("game", Game);
            seed.Parameters.AddWithValue("binding", bindingId);
            seed.Parameters.AddWithValue("state", state);
            seed.Parameters.AddWithValue("partition", Partition);
            seed.Parameters.AddWithValue("expired", expired);
            seed.Parameters.AddWithValue("reservation", reservationId ?? (object)DBNull.Value);
            await seed.ExecuteNonQueryAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public TestServerCallContext Context(string role, string instance) =>
            Certificates.Context(Certificates.Issue("@cluster", role, instance));

        public async Task Rejects(Func<Task> call, StatusCode code, string? detail)
        {
            var error = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal(code, error.Status.StatusCode);
            if (detail is not null) Assert.Equal(detail, error.Status.Detail);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_queue_face_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            // DROP ... WITH (FORCE) collects the database's backends under one lock and
            // checks each pid's privileges after releasing it: a backend that exits in
            // between can have its OS pid reused by another role's connection under a
            // co-tenant fork storm, and the privilege check then lands on that stranger
            // and fails with 42501. The exiting backend is really gone within
            // milliseconds, so a short bounded retry settles it.
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}
