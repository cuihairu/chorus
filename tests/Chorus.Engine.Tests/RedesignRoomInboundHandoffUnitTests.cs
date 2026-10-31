using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-03, seat half: the room actor's inbound handoff. A directly pushed event that
/// the durable leg committed takes one in-memory seat; seating is idempotent per event id,
/// the list is bounded (a fresh event beyond the bound finds no seat — the edge answers
/// Overloaded before any durable write), an idempotent replay of an already seated event
/// never blocks on capacity, and the wake is a coalescing notification that never throws.
/// The loop is never started here: the seat list is the edge's capacity surface, not the
/// consumption path — the recoverable inbox cursor stays the consumption authority.
/// </summary>
public sealed class RedesignRoomInboundHandoffUnitTests
{
    [Fact]
    public void SeatsAreIdempotentPerEventIdAndBoundedPerRoom()
    {
        var actor = NewRoom(inboundBound: 2);

        // Two fresh events take the two seats; the depth says so.
        Assert.True(actor.TryHand("gift-1", "binding-host-ib", """{"action":"boost"}""", "simulator"));
        Assert.True(actor.TryHand("gift-2", null, """{"action":"boost"}""", "simulator"));
        Assert.Equal(2, actor.InboundBacklog);

        // A third fresh event finds no seat — the edge's Overloaded condition.
        Assert.False(actor.TryHand("gift-3", null, """{"action":"boost"}""", "simulator"));
        Assert.Equal(2, actor.InboundBacklog);

        // Seating is idempotent per event id: the same id keeps its single seat, and an
        // already seated id always has capacity — an idempotent replay of a committed
        // event never blocks on a full list.
        Assert.True(actor.TryHand("gift-1", "binding-host-ib", """{"action":"boost"}""", "simulator"));
        Assert.Equal(2, actor.InboundBacklog);
        Assert.True(actor.HasInboundSeat("gift-1"));
        Assert.True(actor.HasInboundSeat("gift-2"));

        // A fresh event on a full list may not even ask — this is the check the edge
        // runs before any durable write.
        Assert.False(actor.HasInboundSeat("gift-3"));

        // The wake is a coalescing notification: bursts never throw, never grow.
        actor.Wake();
        actor.Wake();
        actor.Wake();
    }

    [Fact]
    public void AZeroBoundSeatsNothingAndRefusesEverythingVisibly()
    {
        var actor = NewRoom(inboundBound: 0);

        // Zero closes every room's seats the fail-closed way: no event takes a seat and
        // no event id passes the capacity check — every push answers Overloaded.
        Assert.False(actor.HasInboundSeat("gift-1"));
        Assert.False(actor.TryHand("gift-1", null, """{"action":"boost"}""", "simulator"));
        Assert.Equal(0, actor.InboundBacklog);
    }

    private static RoomActor NewRoom(int inboundBound)
    {
        var module = new ExampleGameModule(new RedesignGameModuleManifest(
            "inbound-seat-tests", "1.0.0-test", 25, 100,
            RedesignGameModuleManifest.DeterministicReplay));
        var participants = new List<GameParticipant>
        {
            new("host-ib", GameStartConfig.RedFaction), new("guest-ib", GameStartConfig.BlueFaction)
        };
        var initial = module.CreateInitialState(new GameStartConfig(200, 777, participants));
        var bindings = new Dictionary<string, string> { ["binding-host-ib"] = "host-ib" };
        return new RoomActor(new ScriptedLifecycle(), new ScriptedFrameStore(), module,
            "game-ib", "room-ib", "game-a", 1, "cfg-ib", initial, bindings, inboundBound: inboundBound);
    }

    private sealed class ScriptedLifecycle : IRoomLifecycle
    {
        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient;
}
