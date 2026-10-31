using Chorus.PlatformGateway;
using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-03, gateway half: the routing cache answers resolves only from a version the
/// control plane heard this gateway confirm. Applying a snapshot closes the gate until the
/// confirm lands; a version that changes underneath the confirmed one closes it again; a
/// confirm that cannot land keeps it closed — an unconfirmed picture guesses no rooms, so the
/// face's pushes wait in the durable leg instead.
/// </summary>
public sealed class RedesignGatewayRoutingConfirmUnitTests
{
    [Fact]
    public async Task AnUnconfirmedSnapshotAnswersNoResolvesUntilTheConfirmLands()
    {
        var confirmed = new List<long>();
        var monitor = new GatewayRoutingMonitor(
            () => throw new InvalidOperationException("The control plane is not part of this test."),
            (version, _) => { confirmed.Add(version); return Task.FromResult(true); });

        monitor.ApplySnapshot(Snapshot(101, endpoint: "https://localhost:5101"));
        // Applied but unconfirmed: the room is in the cache, the gate is closed.
        Assert.False(monitor.TryResolve("simulator", "live-key-a", out _));

        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        Assert.Equal([101], confirmed);
        Assert.True(monitor.TryResolve("simulator", "live-key-a", out var target));
        Assert.Equal("room-confirm-1", target.RoomId);
        Assert.Equal("https://localhost:5101", target.Endpoint);
        // FIX-END-06: the resolve carries the binding's round generation from the same
        // confirmed picture — the claim the face's pushes state at the receiver.
        Assert.Equal(3L, target.RoundGeneration);

        // The world changes: the new version closes the gate until it is confirmed too —
        // the previously confirmed picture must not answer for a newer one.
        monitor.ApplySnapshot(Snapshot(202, endpoint: "https://localhost:5102"));
        Assert.False(monitor.TryResolve("simulator", "live-key-a", out _));
        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        Assert.True(monitor.TryResolve("simulator", "live-key-a", out var moved));
        Assert.Equal("https://localhost:5102", moved.Endpoint);
        Assert.Equal([101, 202], confirmed);
    }

    [Fact]
    public async Task AConfirmThatCannotLandKeepsTheGateClosed()
    {
        var monitor = new GatewayRoutingMonitor(
            () => throw new InvalidOperationException("The control plane is not part of this test."),
            (_, _) => Task.FromResult(false));

        monitor.ApplySnapshot(Snapshot(303, endpoint: "https://localhost:5103"));
        await monitor.ConfirmAppliedAsync(CancellationToken.None);
        Assert.False(monitor.TryResolve("simulator", "live-key-a", out _));
        // The gate reopens only when a later confirm succeeds — the failed one left the
        // applied version standing and the resolve refused.
        var opened = new GatewayRoutingMonitor(
            () => throw new InvalidOperationException("The control plane is not part of this test."),
            (_, _) => Task.FromResult(true));
        opened.ApplySnapshot(Snapshot(303, endpoint: "https://localhost:5103"));
        await opened.ConfirmAppliedAsync(CancellationToken.None);
        Assert.True(opened.TryResolve("simulator", "live-key-a", out _));
    }

    private static RoutingSnapshotReply Snapshot(long version, string endpoint) => new()
    {
        Version = version,
        Rooms =
        {
            new RoomRouteProto
            {
                RoomId = "room-confirm-1", TenantId = "tenant-a", GameId = "game-a",
                OwnerInstanceId = "gs-confirm", Epoch = 4, Endpoint = endpoint, Status = "Active"
            }
        },
        Streamers =
        {
            new StreamerRouteProto
            {
                StreamerId = "streamer-confirm", BindingId = "binding-confirm",
                RoomId = "room-confirm-1", State = "Running", Epoch = 0,
                Platform = "simulator", CanonicalLiveSessionKey = "live-key-a",
                RoundGeneration = 3
            }
        }
    };
}
