using System.Text.Json;
using Chorus.Control;
using Chorus.Engine.Routing;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

public sealed class MatchControlStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chorus-matching-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly ControlDatabase _database;
    private readonly RoomControlStore _store;
    private readonly List<ConnectionRecord> _connections = [];
    private NodeReport Worker(int capacity = 100) => new("worker", "boot", "machine", "worker", new("localhost", 3000), capacity);
    private NodeReport Gateway() => new("gateway", "gw-boot", "machine-g", "gateway", new("localhost", 4000), 0);

    public MatchControlStoreTests()
    {
        _database = new(Path.Combine(_dir, "control.db"));
        _store = new(_database, _clock);
        Heartbeat();
    }

    private void Heartbeat()
    {
        _store.ReportNode(Worker());
        _store.ReportNode(Gateway());
        _store.RenewConnections("gateway", "gw-boot", _connections);
    }

    private void Player(string id, int rating = 1000, string platform = "test", string mode = "default", string region = "default")
    {
        _store.SaveProfile(new(id, id, platform, id, Rating: rating), "test");
        Connect("lobby-" + id, id);
        _store.Join(id, new(mode, region), "test");
    }

    private ConnectionRecord Connect(string room, string player)
    {
        var c = new ConnectionRecord(room, player, player, "streamer", "gateway", "gw-boot", Guid.NewGuid().ToString("N"));
        _store.Connect(c);
        _connections.Add(c);
        return c;
    }

    private void Commit(RoomRecord room, params string[] consumed) => _store.Commit(room.Id,
        new(room.WorkerId, room.WorkerInstance, room.Epoch, room.TickId, room.TickId + 1, 1,
            JsonSerializer.SerializeToElement(new { tick = room.TickId + 1 }), consumed));

    private MatchRecord Pair()
    {
        Player("a"); Player("b");
        return Assert.Single(_store.FormMatches());
    }

    private void Start(MatchRecord match)
    {
        foreach (var id in match.StreamerIds) Connect(match.RoomIds[0], id);
        Commit(_store.Room(match.RoomIds[0])!);
        _store.MaintainMatches();
        Assert.Equal("Running", Assert.Single(_store.Matches()).Status);
    }

    [Fact]
    public void AllocationWaitsForBothParticipantsAndDurableWorkerBeforeRunning()
    {
        var match = Pair();
        Assert.Equal("AwaitingReady", match.Status);
        Assert.Empty(_store.Queue());
        Assert.Throws<ControlConflictException>(() => _store.Join("a", new(), "test"));
        Assert.Throws<ControlConflictException>(() => _store.Finish(match.Id, new(null, "result"), "test"));
        _store.MaintainMatches();
        Connect(match.RoomIds[0], "a");
        Commit(_store.Room(match.RoomIds[0])!);
        _store.MaintainMatches();
        Assert.Equal("AwaitingReady", Assert.Single(_store.Matches()).Status);
        Connect(match.RoomIds[0], "b");
        _store.MaintainMatches();
        Assert.Equal("Running", Assert.Single(_store.Matches()).Status);
        Assert.Empty(_store.FormMatches());
    }

    [Fact]
    public void ConnectionsAloneDoNotProveWorkerReadiness()
    {
        var match = Pair();
        foreach (var p in match.StreamerIds) Connect(match.RoomIds[0], p);
        _store.MaintainMatches();
        Assert.Equal("AwaitingReady", Assert.Single(_store.Matches()).Status);
    }

    [Fact]
    public void AdmissionTimeoutRequeuesOnlinePlayersWithOriginalAgeAndReclaimsRoom()
    {
        var match = Pair();
        var original = match.QueueEntries!.ToDictionary(e => e.StreamerId, e => e.JoinedAtMs);
        _clock.Advance(120_000); Heartbeat();
        _store.MaintainMatches();
        Assert.Equal("Cancelled", Assert.Single(_store.Matches()).Status);
        Assert.Equal(RoomLifecycleState.Destroyed, _store.Room(match.RoomIds[0])!.State);
        Assert.Equal(2, _store.Queue().Count);
        Assert.All(_store.Queue(), q => Assert.Equal(original[q.StreamerId], q.JoinedAtMs));
        _store.MaintainMatches();
        Assert.Equal(2, _store.Queue().Count);
        Assert.Single(_store.FormMatches()); // cancelled proposals do not trigger rematch cooldown
    }

    [Fact]
    public void FinishDrainsExistingInputAndConnectionsBeforeReleasingCapacity()
    {
        var match = Pair(); Start(match);
        var id = match.RoomIds[0];
        var e = new AudienceEvent("pending", "test", id, "viewer", AudienceEventKind.Like, 1);
        _store.AppendEvent(e);
        _store.Finish(match.Id, new("a", "result"), "test");
        Assert.True(_store.Room(id)!.RetirementRequested);
        Assert.NotEqual(RoomLifecycleState.Destroyed, _store.Room(id)!.State);
        Assert.Throws<ControlConflictException>(() => _store.AppendEvent(e with { EventId = "late" }));
        Assert.Throws<ControlConflictException>(() => Connect(id, "late"));
        _store.MaintainMatches();
        Assert.Equal(1, _store.PendingCount(id));
        foreach (var c in _connections.Where(c => c.SessionId == id).ToArray())
        { _store.Disconnect(c); _connections.Remove(c); }
        _store.MaintainMatches();
        Assert.NotEqual(RoomLifecycleState.Destroyed, _store.Room(id)!.State);
        var old = _store.Room(id)!;
        Commit(old, RoomControlStore.EventKey(e));
        _store.MaintainMatches();
        Assert.Equal(RoomLifecycleState.Destroyed, _store.Room(id)!.State);
        Assert.Throws<ControlConflictException>(() => Commit(old));
        _store.Finish(match.Id, new("a", "retry"), "test");
        Assert.Equal(1, _store.Streamers().Single(p => p.Id == "a").Games);
        Assert.Throws<ControlConflictException>(() => _store.Finish(match.Id, new("b", "different"), "test"));
    }

    [Fact]
    public void CapacityFailurePreservesBothQueueEntries()
    {
        Player("a"); Player("b");
        _store.ReportNode(Worker(capacity: 2));
        Assert.Empty(_store.FormMatches());
        Assert.Equal(2, _store.Queue().Count);
        Assert.Equal(2, _store.Rooms().Count);
    }

    [Fact]
    public void CooldownStartsAtSettlementRatherThanMatchCreation()
    {
        var match = Pair(); Start(match);
        _clock.Advance(120_000); Heartbeat();
        _store.Finish(match.Id, new("a", "result"), "test");
        _store.Join("a", new(), "test"); _store.Join("b", new(), "test");
        Assert.Empty(_store.FormMatches());
        _clock.Advance(60_000); Heartbeat();
        Assert.Single(_store.FormMatches());
    }

    [Fact]
    public void ModesRegionsPlatformsAndRatingGapAreHardConstraints()
    {
        Player("a"); Player("mode", mode: "other"); Player("region", region: "other");
        Player("platform", platform: "other"); Player("rating", rating: 2000);
        Assert.Empty(_store.FormMatches());
        _clock.Advance(30_000); Heartbeat();
        Assert.Empty(_store.FormMatches());
    }

    [Fact]
    public void CrossTierRequiresBothWaitersAndBotConfigurationFailsVisibly()
    {
        Player("a", 1199);
        _clock.Advance(30_000); Heartbeat();
        Player("b", 1200);
        Assert.Empty(_store.FormMatches());
        _clock.Advance(30_000); Heartbeat();
        Assert.True(Assert.Single(_store.FormMatches()).CrossTier);
        Assert.Throws<ArgumentException>(() => _store.SetRules(_store.Rules() with { FillBots = true }, "test"));
    }

    [Fact]
    public async Task ConcurrentMatchersCannotConsumePlayersTwice()
    {
        Player("a"); Player("b");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            new RoomControlStore(new ControlDatabase(Path.Combine(_dir, "control.db")), _clock).FormMatches())));
        Assert.Single(results.SelectMany(r => r));
        Assert.Single(_store.Matches());
        Assert.Empty(_store.Queue());
    }

    [Fact]
    public void LegacyFullBoardAcceptsNewReceiptsAndDedupSurvivesReopen()
    {
        var legacy = Enumerable.Range(0, 100_000).Select(i => "old-" + i).ToHashSet();
        _database.Write(t => { t.Put("board", "global:all", new StoredBoard("global:all", "global", "wins", "default",
            new Dictionary<string, long> { ["a"] = 7 }, legacy, 0)); return true; });
        var change = new ScoreChange("a", 1, "new", "wins");
        Assert.Equal(8, _store.ChangeScore("global:all", "global", "default", change, "test").Scores["a"]);
        var reopened = new RoomControlStore(new ControlDatabase(Path.Combine(_dir, "control.db")), _clock);
        Assert.Equal(8, reopened.ChangeScore("global:all", "global", "default", change, "test").Scores["a"]);
        Assert.Equal(8, reopened.ChangeScore("global:all", "global", "default", change with { SourceId = "old-1" }, "test").Scores["a"]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
    private sealed class Clock : TimeProvider
    {
        private long _now = 1_800_000_000_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(_now);
        public void Advance(long ms) => _now += ms;
    }
}
