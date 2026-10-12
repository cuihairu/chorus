using Chorus.Engine.Routing;

namespace Chorus.Engine.Tests;

/// <summary>
/// Room stickiness rule and shared route table (process-topology.md §5.1/§5.3,
/// stage-3 code half): the mapping session → worker must be a deterministic
/// function so a room can never have two writers in a multi-worker deployment.
/// Today's single worker is the degenerate case every room maps to. No
/// production wiring yet — consumers (room-manager, gateway pre-routing) land
/// with stage 3; these tests pin the rule so the wiring can only adopt the
/// invariant, not redefine it.
/// </summary>
public sealed class RoomAffinityTests
{
    [Fact]
    public void Assign_Deterministic_AndOrderIndependent()
    {
        var workers = new[] { "worker-a", "worker-b", "worker-c" };
        var shuffled = new[] { "worker-c", "worker-a", "worker-b" };

        foreach (var sessionId in new[] { "room-1", "room-2", "直播间42", "s-x" })
        {
            var a = RoomAffinity.Assign(sessionId, workers);
            var b = RoomAffinity.Assign(sessionId, shuffled);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Assign_SingleWorker_TodayDegenerateCase()
    {
        Assert.Equal("worker-a", RoomAffinity.Assign("any-room", new[] { "worker-a" }));
        Assert.Equal("worker-a", RoomAffinity.Assign("other-room", new[] { "worker-a" }));
    }

    [Fact]
    public void Assign_SpreadsAcrossWorkers()
    {
        var workers = new[] { "worker-a", "worker-b", "worker-c" };
        var used = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            used.Add(RoomAffinity.Assign($"room-{i}", workers)!);
        }

        Assert.Equal(workers.Length, used.Count);
    }

    [Fact]
    public void Assign_EmptyWorkerSet_NoRoute()
    {
        Assert.Null(RoomAffinity.Assign("room-1", Array.Empty<string>()));
    }

    [Fact]
    public void Assign_IsAFunction_OneRoomOneWorker()
    {
        // The §5.1 invariant: same room, same set → exactly one worker, so
        // there is never a second writer for the room's inputs.
        var workers = new[] { "worker-a", "worker-b", "worker-c", "worker-d" };
        for (var i = 0; i < 50; i++)
        {
            var sessionId = $"room-{i}";
            Assert.Equal(RoomAffinity.Assign(sessionId, workers), RoomAffinity.Assign(sessionId, workers));
        }
    }

    [Fact]
    public void RouteTable_Replace_SwapsWholeMap_SnapshotIsCopy()
    {
        var table = new RoomRouteTable();
        table.Replace(new Dictionary<string, string> { ["room-1"] = "worker-a" });

        var snapshot = table.Snapshot();
        Assert.Equal("worker-a", table.TryGetWorker("room-1", out var worker) ? worker : null);
        Assert.Equal("worker-a", worker);

        table.Replace(new Dictionary<string, string> { ["room-1"] = "worker-b", ["room-2"] = "worker-a" });

        // Old snapshot stays a consistent view; new reads see the new table.
        Assert.Equal("worker-a", snapshot["room-1"]);
        Assert.Equal("worker-b", table.TryGetWorker("room-1", out var w1) ? w1 : null);
        Assert.Equal("worker-a", table.TryGetWorker("room-2", out var w2) ? w2 : null);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void RouteTable_MissingRoom_NoWorker()
    {
        var table = new RoomRouteTable();
        Assert.False(table.TryGetWorker("room-none", out var worker));
        Assert.Null(worker);
    }
}
