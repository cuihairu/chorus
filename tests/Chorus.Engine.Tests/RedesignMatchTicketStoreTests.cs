using System.Security.Cryptography;
using Chorus.Data.Redesign;
using Chorus.ControlServer;
using Chorus.DataServer;
using Chorus.Redesign.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Match ticket store (M3-1, architecture 7.1/7.3): one active ticket per mount, visible
/// refusals for duplicate enqueue, cancellation that loses visibly to a reservation, and a
/// sweep that retires only stale queued tickets.
/// </summary>
public sealed class RedesignMatchTicketStoreTests
{
    private const string Partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";

    [Fact]
    public async Task EnqueueValidatesTheMountAndIsIdempotent()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var control = new RedesignControlStore(database.ConnectionString);
        var roomStore = new RedesignRoomStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);

        var (p1, b1) = await MountAsync(identityStore, bindingStore, "live-key-p1", "ticket-p1");
        var (p2, b2) = await MountAsync(identityStore, bindingStore, "live-key-p2", "ticket-p2");

        var ticket = await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-1");
        Assert.Equal(RedesignMatchTicketStore.Queued, ticket.State);
        Assert.Equal(1, ticket.Version);
        Assert.False(ticket.Duplicate);
        Assert.Null(ticket.ReservationId);

        // An exact retry replays the committed enqueue; the same operation with another
        // partition conflicts.
        var retry = await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-1");
        Assert.True(retry.Duplicate);
        Assert.Equal(ticket.TicketId, retry.TicketId);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => tickets.EnqueueTicketAsync(
            "tenant-a", "game-a", b1.BindingId, b1.SessionId, Partition + "/other", "enqueue-op-1"));

        // The mount already holds an active ticket, so a second enqueue refuses visibly.
        await Assert.ThrowsAsync<RedesignMatchException>(() => tickets.EnqueueTicketAsync(
            "tenant-a", "game-a", b1.BindingId, b1.SessionId, Partition, "enqueue-op-2"));

        // A session the mount no longer holds, and a binding that does not exist.
        await Assert.ThrowsAsync<SessionRevokedException>(() => tickets.EnqueueTicketAsync(
            "tenant-a", "game-a", b2.BindingId, "session-none", Partition, "enqueue-op-3"));
        await Assert.ThrowsAsync<RedesignMatchException>(() => tickets.EnqueueTicketAsync(
            "tenant-a", "game-a", "binding-none", b2.SessionId, Partition, "enqueue-op-4"));

        // A mount already participating in a round never queues.
        var claim = await control.ClaimOwnershipAsync("tenant-a", "game-a", "room", "room-t-1", "gs-1", 30);
        await roomStore.StartRoundAsync("tenant-a", "game-a", "room-t-1", "gs-1", claim.Epoch,
            [new RoundParticipant(b2.BindingId, p2.StreamerId, "Host")],
            "1.0.0", "cfg-1", "pkg-1", "room-op-1");
        await Assert.ThrowsAsync<RedesignMatchException>(() => tickets.EnqueueTicketAsync(
            "tenant-a", "game-a", b2.BindingId, b2.SessionId, Partition, "enqueue-op-5"));

        // After a cancel the mount queues again with a fresh ticket.
        await tickets.CancelTicketAsync("tenant-a", "game-a", b1.BindingId, "cancel-op-1");
        var again = await tickets.EnqueueTicketAsync("tenant-a", "game-a", b1.BindingId, b1.SessionId,
            Partition, "enqueue-op-6");
        Assert.NotEqual(ticket.TicketId, again.TicketId);
        Assert.Equal(RedesignMatchTicketStore.Queued, again.State);

        var status = await tickets.GetTicketAsync("tenant-a", "game-a", b1.BindingId);
        Assert.NotNull(status);
        Assert.Equal(again.TicketId, status!.TicketId);
        Assert.Null(await tickets.GetTicketAsync("tenant-a", "game-a", "binding-none"));
    }

    [Fact]
    public async Task CancelRacesTheActiveStatesVisibly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);

        var (_, binding) = await MountAsync(identityStore, bindingStore, "live-key-c1", "ticket-c1");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", binding.BindingId, binding.SessionId,
            Partition, "enqueue-op-1");

        var cancelled = await tickets.CancelTicketAsync("tenant-a", "game-a", binding.BindingId, "cancel-op-1");
        Assert.Equal(RedesignMatchTicketStore.Cancelled, cancelled.State);
        Assert.Equal(2, cancelled.Version);

        // The same operation replays; a different one finds no active ticket to cancel.
        var retry = await tickets.CancelTicketAsync("tenant-a", "game-a", binding.BindingId, "cancel-op-1");
        Assert.True(retry.Duplicate);
        await Assert.ThrowsAsync<RedesignMatchException>(() => tickets.CancelTicketAsync(
            "tenant-a", "game-a", binding.BindingId, "cancel-op-2"));

        // A ticket held by a reservation refuses; so does a committed one (no active row).
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", binding.BindingId, binding.SessionId,
            Partition, "enqueue-op-2");
        await SetTicketStateAsync(database.ConnectionString, binding.BindingId, "Reserved");
        await Assert.ThrowsAsync<RedesignMatchException>(() => tickets.CancelTicketAsync(
            "tenant-a", "game-a", binding.BindingId, "cancel-op-3"));
        await SetTicketStateAsync(database.ConnectionString, binding.BindingId, "Preparing");
        await Assert.ThrowsAsync<RedesignMatchException>(() => tickets.CancelTicketAsync(
            "tenant-a", "game-a", binding.BindingId, "cancel-op-4"));
        await SetTicketStateAsync(database.ConnectionString, binding.BindingId, "Committed");
        await Assert.ThrowsAsync<RedesignMatchException>(() => tickets.CancelTicketAsync(
            "tenant-a", "game-a", binding.BindingId, "cancel-op-5"));
    }

    [Fact]
    public async Task ExpirySweepsOnlyStaleQueuedTicketsAndPartitionsDoNotMix()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var tickets = new RedesignMatchTicketStore(database.ConnectionString);

        var (_, stale) = await MountAsync(identityStore, bindingStore, "live-key-s1", "ticket-s1");
        var (_, fresh) = await MountAsync(identityStore, bindingStore, "live-key-s2", "ticket-s2");
        var (_, open) = await MountAsync(identityStore, bindingStore, "live-key-s3", "ticket-s3");
        var (_, other) = await MountAsync(identityStore, bindingStore, "live-key-s4", "ticket-s4");

        await tickets.EnqueueTicketAsync("tenant-a", "game-a", stale.BindingId, stale.SessionId,
            Partition, "enqueue-op-1", DateTime.UtcNow.AddSeconds(-1));
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", fresh.BindingId, fresh.SessionId,
            Partition, "enqueue-op-2", DateTime.UtcNow.AddHours(1));
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", open.BindingId, open.SessionId,
            Partition, "enqueue-op-3");
        await tickets.EnqueueTicketAsync("tenant-a", "game-a", other.BindingId, other.SessionId,
            Partition + "/mobile", "enqueue-op-4");

        // The candidate window excludes the stale ticket and never crosses partitions.
        var window = await tickets.ListQueuedAsync(Partition, 10);
        Assert.Equal(2, window.Count);
        Assert.Contains(window, ticket => ticket.BindingId == fresh.BindingId);
        Assert.Contains(window, ticket => ticket.BindingId == open.BindingId);
        var otherWindow = await tickets.ListQueuedAsync(Partition + "/mobile", 10);
        Assert.Single(otherWindow);
        Assert.Equal(other.BindingId, otherWindow[0].BindingId);

        // The sweep retires exactly the stale ticket and releases its mount slot.
        Assert.Equal(1, await tickets.ExpireQueuedAsync(10));
        Assert.Equal(0, await tickets.ExpireQueuedAsync(10));
        Assert.Equal(RedesignMatchTicketStore.Expired,
            (await tickets.GetTicketAsync("tenant-a", "game-a", stale.BindingId))!.State);
        Assert.Equal(2, (await tickets.ListQueuedAsync(Partition, 10)).Count);
        var requeued = await tickets.EnqueueTicketAsync("tenant-a", "game-a", stale.BindingId,
            stale.SessionId, Partition, "enqueue-op-5");
        Assert.Equal(RedesignMatchTicketStore.Queued, requeued.State);
        Assert.Equal(3, (await tickets.ListQueuedAsync(Partition, 10)).Count);
    }

    private static async Task SetTicketStateAsync(string connectionString, string bindingId, string state)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE match.ticket SET state=@state WHERE binding_id=@binding AND state IN ('Queued','Reserved','Preparing')", connection);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("binding", bindingId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(ResolvedStreamer Streamer, MountGrant Mount)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey,
        string openId)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", openId);
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "platform-room", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        var binding = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a",
            "op-1");
        return (streamer, new MountGrant(binding.BindingId, session.SessionId));
    }

    private sealed record MountGrant(string BindingId, string SessionId);

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m3_tickets_" + Guid.NewGuid().ToString("N");
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
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
