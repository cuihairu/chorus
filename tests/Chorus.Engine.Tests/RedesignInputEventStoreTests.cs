using System.Security.Cryptography;
using Chorus.Data.Redesign;
using Chorus.DataServer;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignInputEventStoreTests
{
    [Fact]
    public async Task BatchIsAtomicIdempotentAndConflictsFailVisibly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        // Restart fault injection: the batch dies after every insert, before the commit.
        var armed = false;
        var eventStore = new RedesignInputEventStore(database.ConnectionString,
            () =>
            {
                if (armed) throw new InvalidOperationException("simulated process death mid-batch");
            });

        var (live, session) = await MountAsync(identityStore, bindingStore, "live-key-ev");
        var mounted = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");

        // Two legal same-amount gifts carry different dedup keys and are never content-deduped.
        var batch = new[]
        {
            new PlatformEventInput("gift-1", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":100}"""),
            new PlatformEventInput("gift-2", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":100}"""),
            new PlatformEventInput("comment-1", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"comment","text":"hi"}""")
        };
        var first = await eventStore.IngestAsync("simulator", "app-1", "batch-1", batch);
        Assert.False(first.Duplicate);
        Assert.Equal(3, first.Receipts.Count);
        Assert.All(first.Receipts, receipt =>
        {
            Assert.False(receipt.Duplicate);
            Assert.Equal(RedesignInputEventStore.Attributed, receipt.Disposition);
            Assert.Equal(mounted.BindingId, receipt.BindingId);
            Assert.Equal(1, receipt.BindingGeneration);
        });
        Assert.Equal("gift-1", first.Receipts[0].SourceDedupKey);

        // An exact operation retry replays the original committed receipts.
        var retry = await eventStore.IngestAsync("simulator", "app-1", "batch-1", batch);
        Assert.True(retry.Duplicate);
        Assert.All(retry.Receipts, receipt => Assert.True(receipt.Duplicate));
        Assert.Equal(first.Receipts[0].BindingId, retry.Receipts[0].BindingId);

        // A different operation redelivering the same events is per-event duplicate.
        var redeliver = await eventStore.IngestAsync("simulator", "app-1", "batch-2",
            [batch[0]]);
        Assert.False(redeliver.Duplicate);
        Assert.True(redeliver.Receipts[0].Duplicate);
        Assert.Equal(mounted.BindingId, redeliver.Receipts[0].BindingId);

        // The same operation id with a different batch content conflicts.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => eventStore.IngestAsync(
            "simulator", "app-1", "batch-1",
            [new PlatformEventInput("gift-3", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":50}""")]));
        // The same dedup key with a different payload conflicts visibly, never re-acks.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => eventStore.IngestAsync(
            "simulator", "app-1", "batch-3",
            [new PlatformEventInput("gift-1", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":999}""")]));
        Assert.Equal(3, await CountEventsAsync(database));

        // A mid-batch death rolls the whole receipt transaction back: no partial batch.
        armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => eventStore.IngestAsync(
            "simulator", "app-1", "batch-4",
            [
                new PlatformEventInput("late-1", RedesignInputEventStore.Durable, "live-key-ev",
                    """{"kind":"gift","amount":10}"""),
                new PlatformEventInput("late-2", RedesignInputEventStore.Durable, "live-key-ev",
                    """{"kind":"gift","amount":20}""")
            ]));
        armed = false;
        Assert.Equal(3, await CountEventsAsync(database));
        // The same operation id retries cleanly (its receipt claim rolled back too).
        var recovered = await eventStore.IngestAsync("simulator", "app-1", "batch-4",
        [
            new PlatformEventInput("late-1", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":10}"""),
            new PlatformEventInput("late-2", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":20}""")
        ]);
        Assert.False(recovered.Duplicate);
        Assert.All(recovered.Receipts, receipt => Assert.False(receipt.Duplicate));
        Assert.Equal(5, await CountEventsAsync(database));
        Assert.Equal(5, await CountDispositionAsync(database, RedesignInputEventStore.Attributed));
    }

    [Fact]
    public async Task AttributionFreezesAtFirstReceiveAndFollowsTheCurrentWindow()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);

        var (live, session) = await MountAsync(identityStore, bindingStore, "live-key-ev");
        var first = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-1");
        await eventStore.IngestAsync("simulator", "app-1", "batch-1",
        [
            new PlatformEventInput("gift-old", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":100}""")
        ]);

        // A switch moves the input window to generation 2.
        var second = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-b", "op-2");

        // A retried old event keeps its first-receive attribution (frozen), a new event follows
        // the current window, and an unknown room parks durable / drops best-effort events.
        var duplicate = await eventStore.IngestAsync("simulator", "app-1", "batch-2",
        [
            new PlatformEventInput("gift-old", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":100}"""),
            new PlatformEventInput("gift-new", RedesignInputEventStore.Durable, "live-key-ev",
                """{"kind":"gift","amount":200}"""),
            new PlatformEventInput("banner", RedesignInputEventStore.BestEffort, "room-none",
                """{"kind":"banner","text":"gg"}"""),
            new PlatformEventInput("gift-lost", RedesignInputEventStore.Durable, "room-none",
                """{"kind":"gift","amount":1}""")
        ]);
        Assert.True(duplicate.Receipts[0].Duplicate);
        Assert.Equal(first.BindingId, duplicate.Receipts[0].BindingId);
        Assert.Equal(1, duplicate.Receipts[0].BindingGeneration);
        Assert.False(duplicate.Receipts[1].Duplicate);
        Assert.Equal(second.BindingId, duplicate.Receipts[1].BindingId);
        Assert.Equal(2, duplicate.Receipts[1].BindingGeneration);
        Assert.Equal(RedesignInputEventStore.BestEffortDropped, duplicate.Receipts[2].Disposition);
        Assert.Null(duplicate.Receipts[2].BindingId);
        Assert.Equal(RedesignInputEventStore.PendingAttribution, duplicate.Receipts[3].Disposition);
        Assert.Null(duplicate.Receipts[3].BindingId);

        var pending = await eventStore.GetPendingAsync("simulator");
        var orphan = Assert.Single(pending);
        Assert.Equal("gift-lost", orphan.SourceDedupKey);
        Assert.Equal("room-none", orphan.CanonicalRoomKey);
    }

    [Fact]
    public async Task PendingInboxReconcilesToTheBindingAndKeepsReceiveTime()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var eventStore = new RedesignInputEventStore(database.ConnectionString);

        // The gift arrives before the room has any binding; it waits in the persistent inbox.
        await eventStore.IngestAsync("simulator", "app-1", "batch-1",
        [
            new PlatformEventInput("gift-early", RedesignInputEventStore.Durable, "live-key-late",
                """{"kind":"gift","amount":77}""")
        ]);
        var pending = Assert.Single(await eventStore.GetPendingAsync("simulator"));
        // jsonb persists the parsed document; text reads back in Postgres canonical form.
        Assert.Equal("""{"kind": "gift", "amount": 77}""", pending.PayloadJson);
        var receivedAt = pending.ReceivedAtUtc;

        // The binding appears later on that room; reconciliation attributes the waiting events.
        var other = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "late-open-id");
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", other.StreamerId, "simulator", "app-1",
            "room-2", "live-key-late");
        var otherSession = await identityStore.LoginAsync(other.StreamerId, "gateway-2", "connection-2",
            RandomNumberGenerator.GetBytes(32));
        var mounted = await bindingStore.StartGameplayAsync("tenant-a", live, otherSession.SessionId, "game-a",
            "op-late");

        Assert.Equal(0, await eventStore.ReconcilePendingAsync("simulator", "live-key-nowhere"));
        Assert.Equal(1, await eventStore.ReconcilePendingAsync("simulator", "live-key-late"));
        Assert.Empty(await eventStore.GetPendingAsync("simulator"));
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var reconciled = new NpgsqlCommand("""
                SELECT disposition,binding_id,binding_generation,received_at
                FROM input.event WHERE platform='simulator' AND source_dedup_key='gift-early'
                """, connection))
            await using (var reader = await reconciled.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(RedesignInputEventStore.Attributed, reader.GetString(0));
                Assert.Equal(mounted.BindingId, reader.GetString(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal(receivedAt, new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero));
            }
        }

        // A redelivery after reconciliation keeps the committed attribution.
        var redelivered = await eventStore.IngestAsync("simulator", "app-1", "batch-2",
        [
            new PlatformEventInput("gift-early", RedesignInputEventStore.Durable, "live-key-late",
                """{"kind":"gift","amount":77}""")
        ]);
        Assert.True(redelivered.Receipts[0].Duplicate);
        Assert.Equal(mounted.BindingId, redelivered.Receipts[0].BindingId);
        // Reconciliation is idempotent: nothing is pending, nothing changes.
        Assert.Equal(0, await eventStore.ReconcilePendingAsync("simulator", "live-key-late"));
    }

    private static async Task<(string Live, SessionGrant Session)> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "event-open-id");
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator", "app-1",
            "room-1", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        return (live, session);
    }

    private static async Task<long> CountEventsAsync(TemporaryDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM input.event", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountDispositionAsync(TemporaryDatabase database, string disposition)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM input.event WHERE disposition=@disposition", connection);
        command.Parameters.AddWithValue("disposition", disposition);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m1_events_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
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
