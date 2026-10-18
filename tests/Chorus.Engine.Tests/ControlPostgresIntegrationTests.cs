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
        var connectionString = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "Set CHORUS_TEST_POSTGRES to an isolated PostgreSQL 16 test database.");
        var roomId = "pg-race-" + Guid.NewGuid().ToString("N");
        var setup = NewStore(connectionString!);
        setup.ReportNode(new("worker-a", "boot-a", "machine-a", "worker", new("127.0.0.1", 3101), 100));

        var assignments = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            NewStore(connectionString!).Assign(new(roomId)))));

        Assert.All(assignments, room => Assert.Equal("worker-a", room.WorkerId));
        Assert.Single(setup.Rooms().Where(room => room.Id == roomId));
    }

    [Fact]
    public void PostgresProvider_PersistsFencedTickAndReliableEventAcrossStoreInstances()
    {
        var connectionString = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "Set CHORUS_TEST_POSTGRES to an isolated PostgreSQL 16 test database.");
        var roomId = "pg-" + Guid.NewGuid().ToString("N");
        var first = NewStore(connectionString!);
        var second = NewStore(connectionString!);
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
