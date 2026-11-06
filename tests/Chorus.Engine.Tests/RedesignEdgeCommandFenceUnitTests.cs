using Chorus.Rpc;
using Chorus.StreamerGateway;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-LINK-09 upstream fence (architecture 6.4): a client game command must name the
/// room the mount's confirmed route points at, at the route's epoch — a client names its own
/// participation, never a guessed placement. The fence is the cheap admission; the host's
/// durable client-command leg stays the authoritative judge of session, participation and
/// rate. These tests pin the fence's data source — the confirmed-picture resolve — and the
/// room/epoch comparison the command handler applies on top.
/// </summary>
public sealed class RedesignEdgeCommandFenceUnitTests
{
    private static readonly RoomRouteProto RoomA = new()
    {
        RoomId = "room-a", TenantId = "tenant-a", GameId = "game-a",
        OwnerInstanceId = "game-1", Epoch = 7, Endpoint = "https://localhost:7000", Status = "Applied"
    };

    private static StreamerRouteProto Mount(string roomId, string binding = "binding-1",
        long bindingGeneration = 3, long roundGeneration = 2) => new()
    {
        StreamerId = "streamer-1", BindingId = binding, RoomId = roomId,
        BindingGeneration = bindingGeneration, RoundGeneration = roundGeneration,
        TenantId = "tenant-a", State = "Applied"
    };

    private static async Task<EdgeRoutingMonitor> ConfirmedAsync(RoomRouteProto? room,
        StreamerRouteProto streamer)
    {
        var monitor = new EdgeRoutingMonitor(
            () => throw new InvalidOperationException("The unit test drives snapshots directly."),
            (_, _) => Task.FromResult(true));
        var snapshot = new RoutingSnapshotReply { Version = 1 };
        if (room is not null) snapshot.Rooms.Add(room);
        snapshot.Streamers.Add(streamer);
        monitor.ApplySnapshotAsync(snapshot);
        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        return monitor;
    }

    [Fact]
    public async Task TheFenceRefusesBeforeThePictureIsConfirmed()
    {
        var monitor = new EdgeRoutingMonitor(
            () => throw new InvalidOperationException("The unit test drives snapshots directly."),
            (_, _) => Task.FromResult(true));
        monitor.ApplySnapshotAsync(new RoutingSnapshotReply
        {
            Version = 1, Rooms = { RoomA }, Streamers = { Mount("room-a") }
        });
        // Applied but not confirmed: the control plane has not acknowledged the picture, so
        // nothing is resolved — a push must never guess (architecture 6.4).
        Assert.Null(monitor.ResolveMount("streamer-1", "binding-1", 3));
    }

    [Fact]
    public async Task TheFenceRefusesForABindingMismatch()
    {
        var monitor = await ConfirmedAsync(RoomA, Mount("room-a"));
        // The mount row names another binding/generation — this streamer is not that mount.
        Assert.Null(monitor.ResolveMount("streamer-1", "binding-other", 3));
        Assert.Null(monitor.ResolveMount("streamer-1", "binding-1", 99));
    }

    [Fact]
    public async Task TheFenceRefusesWhenTheRoomHasNoEndpoint()
    {
        var endpointless = new RoomRouteProto
        {
            RoomId = "room-a", TenantId = "tenant-a", GameId = "game-a",
            OwnerInstanceId = "game-1", Epoch = 7, Status = "Applied"
        };
        var monitor = await ConfirmedAsync(endpointless, Mount("room-a"));
        Assert.Null(monitor.ResolveMount("streamer-1", "binding-1", 3));
    }

    [Fact]
    public async Task TheFenceResolvesTheOwnRoomWithThePushFacts()
    {
        var monitor = await ConfirmedAsync(RoomA, Mount("room-a"));
        var fenced = monitor.ResolveMount("streamer-1", "binding-1", 3);
        Assert.NotNull(fenced);
        // The push facts the gateway forwards to the owning host: the room's endpoint, its
        // epoch, and the owner instance the routing snapshot named — not a guessed placement.
        Assert.Equal("room-a", fenced.RoomId);
        Assert.Equal("https://localhost:7000", fenced.Facts.Endpoint);
        Assert.Equal(7, fenced.Facts.Epoch);
        Assert.Equal("game-1", fenced.Facts.OwnerInstanceId);
        Assert.Equal(3, fenced.BindingGeneration);
        Assert.Equal(2, fenced.RoundGeneration);
    }

    /// <summary>The handler's fence decision, verbatim from HandleGameCommandAsync: the room
    /// match (FenceRoom) plus the epoch check. A command that names another room or a stale
    /// epoch is refused before any push — this pins the contract the host tests exercise.</summary>
    private static EdgeRoutingMonitor.FencedRoom? Fence(EdgeRoutingMonitor monitor, string roomId,
        long epoch, string streamerId, string bindingId, long mountGeneration)
    {
        var fenced = monitor.ResolveMount(streamerId, bindingId, mountGeneration);
        if (fenced is null || !string.Equals(fenced.RoomId, roomId, StringComparison.Ordinal)) return null;
        if (fenced.Facts.Epoch != epoch) return null;
        return fenced;
    }

    [Fact]
    public async Task TheFenceRefusesARoomTheMountDoesNotPlay()
    {
        var monitor = await ConfirmedAsync(RoomA, Mount("room-a"));
        // The mount sits in room-a; a command naming room-b is refused before any push.
        Assert.Null(Fence(monitor, "room-b", 7, "streamer-1", "binding-1", 3));
        // A stale epoch on the mount's own room is refused the same way.
        Assert.Null(Fence(monitor, "room-a", 8, "streamer-1", "binding-1", 3));
        // The mount's own room at its own epoch resolves.
        Assert.NotNull(Fence(monitor, "room-a", 7, "streamer-1", "binding-1", 3));
    }
}
