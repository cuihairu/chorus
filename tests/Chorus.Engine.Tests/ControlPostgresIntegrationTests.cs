using System.Text.Json;
using Chorus.Control;
using Chorus.Engine.Routing;
using Chorus.Messaging.Ipc;

namespace Chorus.Engine.Tests;

public sealed class ControlPostgresIntegrationTests
{
    [Fact]
    public void PostgresProvider_PersistsFencedTickAndReliableEventAcrossStoreInstances()
    {
        var connectionString = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "Set CHORUS_TEST_POSTGRES to an isolated PostgreSQL 16 test database.");
        var roomId = "pg-" + Guid.NewGuid().ToString("N");
        var first = new RoomControlStore(new ControlDatabase(connectionString!, ControlDatabaseProvider.PostgreSql));
        var second = new RoomControlStore(new ControlDatabase(connectionString!, ControlDatabaseProvider.PostgreSql));
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
}
