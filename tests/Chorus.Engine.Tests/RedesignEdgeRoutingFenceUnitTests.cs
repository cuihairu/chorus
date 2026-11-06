using Chorus.Rpc;
using Chorus.StreamerGateway;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-LINK-08 gate on the edge's room resolution: a mount resolves a room only from a
/// picture the control plane acknowledged, and only when the snapshot row is the mount's own
/// binding at its own generation. The gateway never guesses a placement (room-recovery.md:39;
/// architecture.md:279 — 缺失或未确认路由不能猜测玩家所在房间).
/// </summary>
public sealed class RedesignEdgeRoutingFenceUnitTests
{
    private static StreamerRouteProto Streamer(
        string streamerId = "streamer-1", string bindingId = "binding-1", string roomId = "room-9",
        long bindingGeneration = 5, long roundGeneration = 7, string tenantId = "t1",
        string state = "Committed")
        => new()
        {
            StreamerId = streamerId, BindingId = bindingId, RoomId = roomId,
            BindingGeneration = bindingGeneration, RoundGeneration = roundGeneration,
            TenantId = tenantId, State = state
        };

    private static RoomRouteProto Room(string roomId = "room-9", string tenantId = "t1",
        string gameId = "game-1", string endpoint = "host:9000", long epoch = 42,
        string status = "Serving")
        => new()
        {
            RoomId = roomId, TenantId = tenantId, GameId = gameId, Endpoint = endpoint,
            Epoch = epoch, Status = status
        };

    private static RoutingSnapshotReply Snapshot(long version, StreamerRouteProto[] streamers,
        RoomRouteProto[]? rooms = null)
    {
        var snapshot = new RoutingSnapshotReply { Version = version };
        snapshot.Rooms.AddRange(rooms ?? [Room()]);
        snapshot.Streamers.AddRange(streamers);
        return snapshot;
    }

    private static EdgeRoutingMonitor Monitor(
        Func<long, CancellationToken, Task<bool>>? confirm = null,
        List<long>? asked = null)
        => new(
            () => throw new InvalidOperationException("The unit test drives snapshots directly."),
            confirm ?? ((version, _) =>
            {
                asked?.Add(version);
                return Task.FromResult(true);
            }));

    [Fact]
    public async Task EdgeRoutingMonitorAsksTheControlPlaneToConfirmTheAppliedVersion()
    {
        var asked = new List<long>();
        var monitor = Monitor(asked: asked);
        var notices = new List<string>();
        monitor.Subscribe("streamer-1", "connection-1",
            room => { notices.Add(room); return Task.CompletedTask; });

        // An applied but unacknowledged picture never produces a push.
        monitor.ApplySnapshotAsync(Snapshot(1, [Streamer(roomId: "room-9")]));
        Assert.Empty(notices);
        Assert.Equal((1L, 0L), monitor.Versions);

        // Confirming asks the control plane for exactly the version that is applied, and only
        // then does the diff reach the subscriber.
        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        Assert.Equal([1L], asked);
        Assert.Equal(["room-9"], notices);
        Assert.Equal((1L, 1L), monitor.Versions);
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("binding-mismatch")]
    [InlineData("mount-generation-mismatch")]
    [InlineData("unconfirmed")]
    [InlineData("room-row-missing")]
    [InlineData("empty-endpoint")]
    [InlineData("empty-room")]
    public async Task AnUnconfirmedOrStaleMountResolvesNoRoom(string scenario)
    {
        var monitor = Monitor();
        var rooms = new[]
        {
            Room(endpoint: scenario == "empty-endpoint" ? "" : "host:9000")
        };
        monitor.ApplySnapshotAsync(Snapshot(1,
        [
            Streamer(
                bindingId: scenario == "binding-mismatch" ? "binding-other" : "binding-1",
                bindingGeneration: scenario == "mount-generation-mismatch" ? 6 : 5,
                roomId: scenario == "empty-room" ? "" : "room-9")
        ], scenario == "room-row-missing" ? [] : rooms));
        // Everything but "unconfirmed" confirms; the unconfirmed case stays with a closed gate.
        if (scenario != "unconfirmed")
            await monitor.ConfirmAppliedAsync(CancellationToken.None);

        var resolved = monitor.ResolveMount("streamer-1", "binding-1", 5);
        if (scenario == "matching")
        {
            Assert.NotNull(resolved);
            Assert.Equal("room-9", resolved.RoomId);
            Assert.Equal("t1", resolved.Facts.TenantId);
            Assert.Equal("host:9000", resolved.Facts.Endpoint);
            Assert.Equal(42, resolved.Facts.Epoch);
            Assert.Equal(5, resolved.BindingGeneration);
            Assert.Equal(7, resolved.RoundGeneration);
        }
        else
        {
            Assert.Null(resolved);
        }
    }

    [Fact]
    public async Task AConfirmThatCannotLandKeepsTheGateClosed()
    {
        var allow = false;
        var monitor = Monitor((_, _) => Task.FromResult(allow));
        monitor.ApplySnapshotAsync(Snapshot(1, [Streamer()]));
        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        Assert.Equal((1L, 0L), monitor.Versions);
        Assert.Null(monitor.ResolveMount("streamer-1", "binding-1", 5));

        // The control plane's answer lands; only then does the mount resolve.
        allow = true;
        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        Assert.Equal((1L, 1L), monitor.Versions);
        Assert.NotNull(monitor.ResolveMount("streamer-1", "binding-1", 5));
    }

    [Fact]
    public async Task AChangedVersionClosesTheGateUntilTheNewOneIsConfirmed()
    {
        var monitor = Monitor();
        monitor.ApplySnapshotAsync(Snapshot(1, [Streamer(roomId: "room-9")]));
        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        Assert.Equal("room-9", monitor.ResolveMount("streamer-1", "binding-1", 5)?.RoomId);

        // A newer world is applied but not yet acknowledged: the old confirmed picture no
        // longer resolves, and nothing is guessed from the unconfirmed row.
        monitor.ApplySnapshotAsync(Snapshot(2, [Streamer(roomId: "room-10")],
            [Room(roomId: "room-10")]));
        Assert.Equal((2L, 1L), monitor.Versions);
        Assert.Null(monitor.ResolveMount("streamer-1", "binding-1", 5));

        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        Assert.Equal((2L, 2L), monitor.Versions);
        Assert.Equal("room-10", monitor.ResolveMount("streamer-1", "binding-1", 5)?.RoomId);
    }
}
