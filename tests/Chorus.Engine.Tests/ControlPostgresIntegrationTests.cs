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
