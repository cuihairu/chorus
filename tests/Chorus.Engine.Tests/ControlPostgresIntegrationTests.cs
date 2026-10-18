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
    }

    private static RoomControlStore NewStore(string connectionString) =>
        new(new ControlDatabase(connectionString, ControlDatabaseProvider.PostgreSql));
}
