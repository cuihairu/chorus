using System.Text.Json;
using Chorus.Control;
using Chorus.Engine.Routing;
using Chorus.Messaging.Ipc;

namespace Chorus.Engine.Tests;

public sealed class ControlPostgresIntegrationTests
{
    [Fact]
    public async Task PostgresProvider_ConcurrentAssignmentsKeepOneAuthoritativeRoomRecord()
    {
        using var testDatabase = new PostgresTestDatabase();
        var connectionString = testDatabase.ConnectionString;
        var roomId = "pg-race-" + Guid.NewGuid().ToString("N");
        var setup = NewStore(connectionString);
        setup.ReportNode(new("worker-a", "boot-a", "machine-a", "worker", new("127.0.0.1", 3101), 100));

        var assignments = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            NewStore(connectionString).Assign(new(roomId)))));

        Assert.All(assignments, room => Assert.Equal("worker-a", room.WorkerId));
        Assert.Single(setup.Rooms().Where(room => room.Id == roomId));
    }

    [Fact]
    public async Task PostgresProvider_ConcurrentRoomEventsKeepAllReceipts()
    {
        using var testDatabase = new PostgresTestDatabase();
        var store = NewStore(testDatabase.ConnectionString);
        var prefix = "pg-events-" + Guid.NewGuid().ToString("N");
        store.ReportNode(new(prefix + "-worker", prefix + "-boot", prefix + "-machine", "worker", new("127.0.0.1", 3101), 100));
        var room = store.Assign(new(prefix));

        var accepted = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            store.AppendEvent(new("event-" + index, "test", prefix, "viewer", Chorus.Protocol.Models.AudienceEventKind.Like, 1)))));

        Assert.All(accepted, Assert.True);
        Assert.Equal(32, store.PendingCount(prefix));
        Assert.Equal(32, store.Prepare(prefix, new(room.WorkerId, room.WorkerInstance, room.Epoch)).Inbox!.Count);
        Assert.Equal(32, store.EventHeat().Single().Events);
    }

    [Fact]
    public void PostgresProvider_PersistsFencedTickAndReliableEventAcrossStoreInstances()
    {
        using var testDatabase = new PostgresTestDatabase();
        var connectionString = testDatabase.ConnectionString;
        var roomId = "pg-" + Guid.NewGuid().ToString("N");
        var first = NewStore(connectionString);
        var second = NewStore(connectionString);
        first.ReportNode(new("worker-a", "boot-a", "machine-a", "worker", new("127.0.0.1", 3101), 4));

        var room = first.Assign(new(roomId));
        var owner = new OwnerRequest(room.WorkerId, room.WorkerInstance, room.Epoch);
        var evt = new Chorus.Protocol.Models.AudienceEvent("event-" + roomId, "test", roomId, "viewer", Chorus.Protocol.Models.AudienceEventKind.Like, 1);
        Assert.True(first.AppendEvent(evt));

        var prepared = second.Prepare(roomId, owner);
        Assert.Single(prepared.Inbox!);
        var state = JsonSerializer.SerializeToElement(new { count = 1 });
        Assert.True(first.Commit(roomId, new(room.WorkerId, room.WorkerInstance, room.Epoch, 0, 1, 1, state, prepared.Inbox!.Select(RoomControlStore.EventKey).ToArray())));

        var restored = second.Room(roomId, checkpoint: true)!;
        Assert.Equal(1, restored.TickId);
        Assert.Equal(1, restored.Snapshot!.Value.GetProperty("count").GetInt32());
        Assert.False(second.AppendEvent(evt));
        var heat = Assert.Single(second.EventHeat());
        Assert.Equal("test", heat.Platform);
        Assert.Equal(1, heat.Events);
    }

    [Fact]
    public void PostgresProvider_MigrationPreservesCheckpointAndPendingInputAndFencesOldOwner()
    {
        using var testDatabase = new PostgresTestDatabase();
        var store = NewStore(testDatabase.ConnectionString);
        var secondStore = NewStore(testDatabase.ConnectionString);
        var prefix = "pg-migration-" + Guid.NewGuid().ToString("N");
        var workerA = prefix + "-a";
        var workerB = prefix + "-b";
        store.ReportNode(new(workerA, "boot-a", "machine-a", "worker", new("127.0.0.1", 3101), 10));
        store.ReportNode(new(workerB, "boot-b", "machine-b", "worker", new("127.0.0.1", 3102), 10));
        var room = store.Assign(new(prefix));
        Assert.Equal(workerA, room.WorkerId);
        var firstEvent = new Chorus.Protocol.Models.AudienceEvent("first", "test", room.Id, "viewer", Chorus.Protocol.Models.AudienceEventKind.Like, 1);
        var pendingEvent = firstEvent with { EventId = "pending" };
        Assert.True(store.AppendEvent(firstEvent));
        var prepared = store.Prepare(room.Id, new(workerA, "boot-a", room.Epoch));
        var state = JsonSerializer.SerializeToElement(new { tick = 1 });
        Assert.True(store.Commit(room.Id, new(workerA, "boot-a", room.Epoch, 0, 1, 1, state,
            prepared.Inbox!.Select(RoomControlStore.EventKey).ToArray())));
        Assert.True(store.AppendEvent(pendingEvent));
        Assert.Throws<ControlConflictException>(() => secondStore.Commit(room.Id,
            new(workerA, "boot-a", room.Epoch, 1, 2, 1, state, ["missing-receipt"])));
        Assert.Equal(1, store.Room(room.Id)!.TickId);
        Assert.Equal(1, store.PendingCount(room.Id));

        var moving = store.Move(room.Id, new(workerB, room.Epoch), "operator");
        var handedOff = store.Handoff(room.Id, moving.Epoch);
        Assert.Equal(RoomLifecycleState.Recovering, handedOff.State);
        Assert.Equal(1, handedOff.TickId);
        Assert.Equal(1, store.PendingCount(room.Id));
        store.Ready(room.Id, new(workerB, "boot-b", handedOff.Epoch));
        Assert.Throws<ControlConflictException>(() => secondStore.Commit(room.Id,
            new(workerA, "boot-a", room.Epoch, 1, 2, 1, state, [])));
        Assert.Equal(1, store.Room(room.Id, checkpoint: true)!.Snapshot!.Value.GetProperty("tick").GetInt32());
        var incoming = store.Prepare(room.Id, new(workerB, "boot-b", handedOff.Epoch));
        Assert.Equal("pending", Assert.Single(incoming.Inbox!).EventId);
        Assert.True(secondStore.Commit(room.Id, new(workerB, "boot-b", handedOff.Epoch, 1, 2, 1, state,
            incoming.Inbox!.Select(RoomControlStore.EventKey).ToArray())));
        Assert.Equal(0, store.PendingCount(room.Id));

        var movingBack = store.Move(room.Id, new(workerA, store.Room(room.Id)!.Epoch), "operator");
        var recovering = store.Handoff(room.Id, movingBack.Epoch);
        var aborted = store.Abort(room.Id, recovering.Epoch, "operator");
        Assert.Equal(workerB, aborted.WorkerId);
        Assert.Equal(recovering.Epoch + 1, aborted.Epoch);
        var restored = store.Ready(room.Id, new(workerB, "boot-b", aborted.Epoch));
        Assert.Equal(RoomLifecycleState.Active, restored.State);
        Assert.Equal(2, restored.TickId);
    }

    [Fact]
    public void PostgresProvider_RequiresConnectionsDrainedBeforeClosingRoom()
    {
        using var testDatabase = new PostgresTestDatabase();
        var store = NewStore(testDatabase.ConnectionString);
        store.ReportNode(new("worker", "worker-boot", "worker-host", "worker", new("127.0.0.1", 3101), 10));
        store.ReportNode(new("gateway", "gateway-boot", "gateway-host", "gateway", new("127.0.0.1", 4101), 0));
        var room = store.Assign(new("pg-drain-" + Guid.NewGuid().ToString("N")));
        var connection = new ConnectionRecord(room.Id, "client", "viewer", "viewer", "gateway", "gateway-boot", "generation");
        store.Connect(connection);

        Assert.Throws<ControlConflictException>(() => store.Action(room.Id,
            new(RoomLifecycleAction.Close, room.Epoch), "operator"));
        Assert.Equal(RoomLifecycleState.Waiting, store.Room(room.Id)!.State);

        store.Disconnect(connection);
        Assert.Equal(RoomLifecycleState.Closing, store.Action(room.Id,
            new(RoomLifecycleAction.Close, room.Epoch), "operator").State);
        Assert.Contains(store.Audits(), audit => audit.Action == "room.lifecycle" && audit.Target == room.Id);
        var auditUsage = store.AuditUsage();
        Assert.True(auditUsage.Rows >= 1);
        Assert.True(auditUsage.ApproximatePayloadBytes > 0);
        Assert.NotNull(auditUsage.OldestAtMs);
        Assert.NotNull(auditUsage.NewestAtMs);
    }

    [Fact]
    public void PostgresProvider_LateDisconnectCannotRemoveNewConnectionGeneration()
    {
        using var testDatabase = new PostgresTestDatabase();
        var store = NewStore(testDatabase.ConnectionString);
        store.ReportNode(new("worker", "worker-boot", "worker-host", "worker", new("127.0.0.1", 3101), 10));
        store.ReportNode(new("gateway", "gateway-boot", "gateway-host", "gateway", new("127.0.0.1", 4101), 0));
        var room = store.Assign(new("pg-generation-" + Guid.NewGuid().ToString("N")));
        var oldConnection = new ConnectionRecord(room.Id, "client", "viewer", "viewer", "gateway", "gateway-boot", "old-generation");
        var newConnection = oldConnection with { Generation = "new-generation" };

        store.Connect(oldConnection);
        store.Connect(newConnection);
        store.Disconnect(oldConnection);

        var remaining = Assert.Single(store.Connections());
        Assert.Equal("new-generation", remaining.Generation);
    }

    [Fact]
    public void PostgresProvider_PersistsEventHeatAndAlertAcknowledgement()
    {
        using var testDatabase = new PostgresTestDatabase();
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = NewStore(testDatabase.ConnectionString, new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(at)));
        store.ReportNode(new("worker", "worker-boot", "worker-host", "worker", new("127.0.0.1", 3101), 10));
        for (var i = 0; i < RoomControlStore.EventHeatAlertThreshold; i++)
            Assert.True(store.AppendEvent(new("pg-heat-" + i, "bilibili", "pg-heat-room-" + (i % 2), "viewer-" + i,
                Chorus.Protocol.Models.AudienceEventKind.Comment, at)));

        var alert = Assert.Single(store.EventHeatAlerts());
        Assert.Equal(RoomControlStore.EventHeatAlertThreshold, alert.Events);
        Assert.True(store.AcknowledgeEventHeatAlert(alert.Platform, alert.BucketAtMs, "operator").Acknowledged);
        Assert.True(Assert.Single(store.EventHeatAlerts()).Acknowledged);
    }

    private static RoomControlStore NewStore(string connectionString, TimeProvider? timeProvider = null) =>
        new(new ControlDatabase(connectionString, ControlDatabaseProvider.PostgreSql), timeProvider);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
