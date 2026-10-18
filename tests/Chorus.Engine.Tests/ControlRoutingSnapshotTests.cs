using Chorus.Control;
using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

public sealed class ControlRoutingSnapshotTests
{
    [Fact]
    public void RoutesOnlyCurrentRoomOwnerAndSubscribedGateways()
    {
        const long now = 1_800_000_000_000;
        var snapshot = Snapshot(now);

        Assert.True(snapshot.TryGetTargets("room", 4, "worker", "worker-boot", now, out var targets));
        Assert.Equal(new[] { "gateway-a" }, targets.Select(n => n.Id));
        Assert.False(snapshot.TryGetTargets("room", 3, "worker", "worker-boot", now, out _));
        Assert.False(snapshot.TryGetTargets("room", 4, "other-worker", "worker-boot", now, out _));
        Assert.False(snapshot.TryGetTargets("missing", 4, "worker", "worker-boot", now, out _));
    }

    [Theory]
    [InlineData(RoomLifecycleState.Migrating)]
    [InlineData(RoomLifecycleState.Recovering)]
    [InlineData(RoomLifecycleState.Closing)]
    [InlineData(RoomLifecycleState.Destroyed)]
    public void RefusesRoomsOutsideWritableStates(RoomLifecycleState state)
    {
        const long now = 1_800_000_000_000;
        var snapshot = Snapshot(now, state);
        Assert.False(snapshot.TryGetTargets("room", 4, "worker", "worker-boot", now, out _));
    }

    [Fact]
    public void ExpiredSnapshotFailsClosed()
    {
        const long now = 1_800_000_000_000;
        var snapshot = Snapshot(now);
        Assert.False(snapshot.TryGetTargets("room", 4, "worker", "worker-boot", now + ControlRoutingSnapshot.MaxAgeMs, out _));
    }

    [Fact]
    public void ExpiredOrReplacedWorkerLeaseCannotPublish()
    {
        const long now = 1_800_000_000_000;
        var expired = Snapshot(now,workerHeartbeat:now-RoomControlStore.NodeLeaseMs);
        Assert.False(expired.TryGetAuthority("room",4,now,out _));
        var replaced = Snapshot(now,workerInstance:"new-boot");
        Assert.False(replaced.TryGetAuthority("room",4,now,out _));
    }

    [Fact]
    public void CurrentRoomAuthorityForRealtimeControlMessagesRequiresFreshSnapshotAndLiveOwner()
    {
        const long now = 1_800_000_000_000;
        var snapshot = Snapshot(now);

        Assert.True(snapshot.TryGetCurrentAuthority("room", now, out var room));
        Assert.Equal("worker", room.WorkerId);
        Assert.False(snapshot.TryGetCurrentAuthority("room", now + ControlRoutingSnapshot.MaxAgeMs, out _));
        Assert.False(Snapshot(now, workerHeartbeat: now - RoomControlStore.NodeLeaseMs)
            .TryGetCurrentAuthority("room", now, out _));
        Assert.False(snapshot.TryGetCurrentAuthority("missing", now, out _));
    }

    [Fact]
    public void HealthyWorkerDoesNotMaskExpiredOwnerOfAnotherRoom()
    {
        const long now = 1_800_000_000_000;
        var snapshot = new ControlRoutingSnapshot(now,
            [new NodeReport("worker-a","a-boot","machine-a","worker",new("127.0.0.1",3101),100,now-RoomControlStore.NodeLeaseMs),
             new NodeReport("worker-b","b-boot","machine-b","worker",new("127.0.0.1",3102),100,now),
             new NodeReport("gateway","g-boot","machine-g","gateway",new("127.0.0.1",4101),0,now)],
            [new RoomRecord("room-a","worker-a","a-boot",1,RoomLifecycleState.Active,now),
             new RoomRecord("room-b","worker-b","b-boot",1,RoomLifecycleState.Active,now)],
            [new ConnectionRecord("room-a","client-a","user-a","streamer","gateway","g-boot","a",now),
             new ConnectionRecord("room-b","client-b","user-b","streamer","gateway","g-boot","b",now)]);

        Assert.False(snapshot.TryGetTargets("room-a",1,"worker-a","a-boot",now,out _));
        Assert.True(snapshot.TryGetTargets("room-b",1,"worker-b","b-boot",now,out var targets));
        Assert.Equal("gateway",Assert.Single(targets).Id);
    }

    [Fact]
    public void CachedSnapshotValidatesConnectAuthorityAndGenerationFailClosed()
    {
        const long now=1_800_000_000_000;
        var snapshot=Snapshot(now);
        Assert.True(snapshot.TryValidateConnect("room",4,"worker","worker-boot","client-a","generation-a","ga-boot",now,out _));
        Assert.False(snapshot.TryValidateConnect("room",4,"worker","worker-boot","client-a","old-generation","ga-boot",now,out _));
        Assert.False(snapshot.TryValidateConnect("room",3,"worker","worker-boot","client-a","generation-a","ga-boot",now,out _));
        Assert.False(snapshot.TryValidateConnect("room",4,"worker","worker-boot","client-a","generation-a","ga-boot",now+ControlRoutingSnapshot.MaxAgeMs,out _));
    }

    private static ControlRoutingSnapshot Snapshot(long now, RoomLifecycleState state = RoomLifecycleState.Active,
        long? workerHeartbeat = null,string workerInstance = "worker-boot") => new(
        now,
        [new NodeReport("gateway-a", "ga-boot", "machine-a", "gateway", new("127.0.0.1", 4101), 0, now),
         new NodeReport("gateway-b", "gb-boot", "machine-b", "gateway", new("127.0.0.1", 4102), 0, now),
         new NodeReport("worker", workerInstance, "machine-w", "worker", new("127.0.0.1", 3101), 100, workerHeartbeat ?? now)],
        [new RoomRecord("room", "worker", "worker-boot", 4, state, now)],
        [new ConnectionRecord("room", "client-a", "user-a", "streamer", "gateway-a", "ga-boot", "generation-a", now),
         new ConnectionRecord("another-room", "client-b", "user-b", "streamer", "gateway-b", "gb-boot", "generation-b", now)]);
}
