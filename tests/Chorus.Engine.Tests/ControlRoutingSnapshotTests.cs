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
