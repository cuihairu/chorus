using System.Text.Json;
using Microsoft.Data.Sqlite;
using Chorus.Data;
using Chorus.Control;
using Chorus.Engine.Routing;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Models;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;

namespace Chorus.Engine.Tests;

public sealed class ControlStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),"chorus-control-"+Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private RoomControlStore NewStore() => new(new ControlDatabase(Path.Combine(_dir,"control.db")),_clock);
    private static NodeReport Worker(string id, int capacity = 10) => new(id,id+"-boot","host-"+id,"worker",new("127.0.0.1",3000+(id[0]-'a'+1)),capacity);
    private static AudienceEvent Event(string id="event-1") => new(id,"test","room","viewer",AudienceEventKind.Like,1);
    private static TickCommit Commit(RoomRecord room,long expected = 0,string worker="a",params string[] consumed) => new(worker,worker+"-boot",room.Epoch,expected,expected+1,1,JsonSerializer.SerializeToElement(new { count = expected+1 }),consumed);
    [Fact]
    public async Task ConcurrentCreationIsStableAcrossExpansionAndDatabaseRestart()
    {
        var s=NewStore(); s.ReportNode(Worker("a"));
        var results=await Task.WhenAll(Enumerable.Range(0,10).Select(_=>Task.Run(()=>NewStore().Assign(new("room")))));
        Assert.All(results,r=>Assert.Equal("a",r.WorkerId));
        s.ReportNode(Worker("b"));
        Assert.Equal("a",s.Assign(new("room")).WorkerId);
        Assert.Equal("b",s.Assign(new("new-room")).WorkerId);
        s.ReportNode(Worker("c"));
        Assert.Equal("c",s.Assign(new("third-room")).WorkerId);
        Assert.Equal("a",s.Assign(new("room")).WorkerId);
        Assert.Equal("a",NewStore().Room("room")!.WorkerId);
        Assert.Equal("c",NewStore().Room("third-room")!.WorkerId);
        Assert.Equal(1,NewStore().Room("room")!.Epoch);
    }
    [Fact]
    public void CapacityAndDrainRejectNewRoomsWithoutChangingExisting()
    {
        var s=NewStore();s.ReportNode(Worker("a",1));var room=s.Assign(new("room"));
        Assert.Throws<ControlConflictException>(()=>s.Assign(new("overflow")));
        s.Drain("a",true,"admin");s.ReportNode(Worker("a",1));
        Assert.True(s.Nodes().Single().Draining);
        Assert.Equal(room,s.Room("room"));
    }
    [Fact]
    public void DrainingWorkerStopsNewAssignmentsButKeepsItsExistingRooms()
    {
        var s=NewStore();s.ReportNode(Worker("a"));s.ReportNode(Worker("b"));
        var existing=s.Assign(new("existing"));
        s.Drain(existing.WorkerId,true,"operator");
        s.ReportNode(Worker("a"));

        var next=s.Assign(new("next"));
        Assert.NotEqual(existing.WorkerId,next.WorkerId);
        Assert.Equal(existing,s.Room("existing"));
    }
    [Fact]
    public void FirstConnectionWakesSleepingRoomThroughRecoveryAndRestoresIdleState()
    {
        var s=NewStore();s.ReportNode(Worker("a"));
        s.ReportNode(new("gateway","gateway-boot","gateway-host","gateway",new("127.0.0.1",4101),0));
        var room=s.Assign(new("room"));
        var idle=s.Action("room",new(RoomLifecycleAction.GoIdle,room.Epoch),"operator");
        s.Commit("room",Commit(idle));
        idle=s.Room("room")!;
        var sleeping=s.Action("room",new(RoomLifecycleAction.Sleep,idle.Epoch),"operator");

        s.Connect(new("room","client","streamer","streamer","gateway","gateway-boot","generation"));

        var waking=s.Room("room")!;
        Assert.Equal(RoomLifecycleState.Recovering,waking.State);
        Assert.Equal(sleeping.Epoch+1,waking.Epoch);
        Assert.Equal(RoomLifecycleState.Idle,waking.ResumeState);
        var resumed=s.Ready("room",new(waking.WorkerId,waking.WorkerInstance,waking.Epoch));
        Assert.Equal(RoomLifecycleState.Idle,resumed.State);
    }
    [Fact]
    public void CannotSleepUntilCheckpointIsDurable()
    {
        var s=NewStore();s.ReportNode(Worker("a"));
        var room=s.Assign(new("room"));
        var idle=s.Action("room",new(RoomLifecycleAction.GoIdle,room.Epoch),"operator");

        Assert.Throws<ControlConflictException>(()=>s.Action("room",new(RoomLifecycleAction.Sleep,idle.Epoch),"operator"));
        Assert.Equal(RoomLifecycleState.Idle,s.Room("room")!.State);
    }
    [Fact]
    public void ReliableEventWakesSleepingRoomAndRemainsPendingUntilRecoveryCompletes()
    {
        var s=NewStore();s.ReportNode(Worker("a"));
        var room=s.Assign(new("room"));
        var idle=s.Action("room",new(RoomLifecycleAction.GoIdle,room.Epoch),"operator");
        s.Commit("room",Commit(idle));
        idle=s.Room("room")!;
        var sleeping=s.Action("room",new(RoomLifecycleAction.Sleep,idle.Epoch),"operator");

        Assert.True(s.AppendEvent(Event()));
        var waking=s.Room("room")!;
        Assert.Equal(RoomLifecycleState.Recovering,waking.State);
        Assert.Equal(sleeping.Epoch+1,waking.Epoch);
        Assert.Equal(1,s.PendingCount("room"));
        Assert.Throws<ControlConflictException>(()=>s.Prepare("room",new(waking.WorkerId,waking.WorkerInstance,waking.Epoch)));

        s.Ready("room",new(waking.WorkerId,waking.WorkerInstance,waking.Epoch));
        Assert.Single(s.Prepare("room",new(waking.WorkerId,waking.WorkerInstance,waking.Epoch)).Inbox!);
    }

    [Fact]
    public void DuplicateReliableEventAcrossGatewaysAndMigrationKeepsOneReceipt()
    {
        var s=NewStore(); s.ReportNode(Worker("a")); s.ReportNode(Worker("b"));
        var room=s.Assign(new("room"));
        var e=Event("same-platform-event");
        Assert.True(s.AppendEvent(e));
        Assert.False(NewStore().AppendEvent(e)); // second Gateway, same durable authority
        var migration=s.Move("room",new("b",room.Epoch),"operator");
        var handoff=s.Handoff("room",migration.Epoch);
        s.Ready("room",new(handoff.WorkerId,handoff.WorkerInstance,handoff.Epoch));
        Assert.False(NewStore().AppendEvent(e)); // receipt survives owner/epoch change
        Assert.Equal(1,s.PendingCount("room"));
        Assert.Single(s.Prepare("room",new(handoff.WorkerId,handoff.WorkerInstance,handoff.Epoch)).Inbox!);
    }
    [Fact]
    public void NewConnectionDuringMigrationIsRetryableAndNotRegistered()
    {
        var s=NewStore();s.ReportNode(Worker("a"));s.ReportNode(Worker("b"));
        s.ReportNode(new("gateway","gateway-boot","gateway-host","gateway",new("127.0.0.1",4101),0));
        var room=s.Assign(new("room"));
        s.Move("room",new("b",room.Epoch),"operator");

        Assert.Throws<ControlConflictException>(()=>s.Connect(new("room","client","streamer","streamer","gateway","gateway-boot","generation")));
        Assert.Empty(s.Connections());
    }
    [Fact]
    public void CloseRequiresConnectionsDrainedBeforeEnteringClosing()
    {
        var s=NewStore();s.ReportNode(Worker("a"));
        s.ReportNode(new("gateway","gateway-boot","gateway-host","gateway",new("127.0.0.1",4101),0));
        var room=s.Assign(new("room"));
        var connection=new ConnectionRecord("room","client","viewer","viewer","gateway","gateway-boot","generation");
        s.Connect(connection);

        Assert.Throws<ControlConflictException>(()=>s.Action("room",new(RoomLifecycleAction.Close,room.Epoch),"operator"));
        Assert.Equal(RoomLifecycleState.Waiting,s.Room("room")!.State);
        s.Disconnect(connection);
        Assert.Equal(RoomLifecycleState.Closing,s.Action("room",new(RoomLifecycleAction.Close,room.Epoch),"operator").State);
    }
    [Fact]
    public void ExplicitResumePreservesStableStateFromBeforeSleep()
    {
        var s=NewStore();s.ReportNode(Worker("a"));
        var room=s.Assign(new("room"));
        var idle=s.Action("room",new(RoomLifecycleAction.GoIdle,room.Epoch),"operator");
        s.Commit("room",Commit(idle));
        idle=s.Room("room")!;
        var sleeping=s.Action("room",new(RoomLifecycleAction.Sleep,idle.Epoch),"operator");
        var recovering=s.Action("room",new(RoomLifecycleAction.Resume,sleeping.Epoch),"operator");

        var resumed=s.Ready("room",new(recovering.WorkerId,recovering.WorkerInstance,recovering.Epoch));
        Assert.Equal(RoomLifecycleState.Idle,resumed.State);
    }
    [Fact]
    public void FencedMigrationRestoresCommittedStateAndPendingInput()
    {
        var s=NewStore();s.ReportNode(Worker("a"));s.ReportNode(Worker("b"));var room=s.Assign(new("room"));
        s.AppendEvent(Event());s.Commit("room",Commit(room,0,"a",RoomControlStore.EventKey(Event())));
        s.AppendEvent(Event("pending"));
        var moving=s.Move("room",new("b",room.Epoch),"operator");
        Assert.Throws<ControlConflictException>(()=>s.Commit("room",Commit(room,1)));
        var incoming=s.Handoff("room",moving.Epoch);
        Assert.Equal(2,incoming.Epoch);Assert.Equal(1,incoming.TickId);Assert.Equal(1,s.PendingCount("room"));
        Assert.Throws<ControlConflictException>(()=>s.Prepare("room",new("b","b-boot",2)));
        s.Ready("room",new("b","b-boot",2));
        Assert.Throws<ControlConflictException>(()=>s.Commit("room",Commit(room,1)));
        Assert.True(s.Commit("room",Commit(incoming,1,"b",RoomControlStore.EventKey(Event("pending")))));
        Assert.Equal(0,NewStore().PendingCount("room"));
        Assert.Throws<ControlConflictException>(()=>s.Commit("room",Commit(incoming,1,"b")));
    }

    [Fact]
    public async Task LiveOldActorLosesWriteAuthorityAfterLeaseTakeoverAndHandoff()
    {
        var s=NewStore();
        s.ReportNode(Worker("a")); s.ReportNode(Worker("b"));
        var room=s.Assign(new("room"));
        using var game=new NoopGame();
        var actor=new SessionActor("room",new SessionConfig { TickDurationMs=10 },game,
            durability:new StoreDurability(s,room),restoredTick:room.TickId);
        actor.Start();
        try
        {
            await WaitUntil(()=>s.Room("room")!.TickId>=1);

            _clock.Advance(RoomControlStore.NodeLeaseMs+1);
            s.ReportNode(Worker("a") with { InstanceId="a-new-boot" });
            s.ReportNode(Worker("b"));
            var migration=s.Move("room",new("b",room.Epoch),"operator");
            var handedOff=s.Handoff("room",migration.Epoch);
            s.Ready("room",new(handedOff.WorkerId,handedOff.WorkerInstance,handedOff.Epoch));
            var checkpointAtHandoff=handedOff.TickId;

            await WaitUntil(()=>actor.DurabilityFailed);
            Assert.Equal(checkpointAtHandoff,s.Room("room")!.TickId);
            Assert.Equal("b",s.Room("room")!.WorkerId);
        }
        finally { await actor.DisposeAsync(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until=DateTime.UtcNow+TimeSpan.FromSeconds(3);
        while(!condition() && DateTime.UtcNow<until) await Task.Delay(5);
        Assert.True(condition(),"Condition did not become true before timeout.");
    }
    private sealed class StoreDurability(RoomControlStore store,RoomRecord owner) : ISessionTickDurability
    {
        public Task<IReadOnlyList<AudienceEvent>> PrepareAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AudienceEvent>>(store.Prepare(owner.Id,new(owner.WorkerId,owner.WorkerInstance,owner.Epoch)).Inbox??[]);
        public Task CommitAsync(long expected,long tick,object state,IReadOnlyList<AudienceEvent> consumed,CancellationToken ct)
        {
            store.Commit(owner.Id,new(owner.WorkerId,owner.WorkerInstance,owner.Epoch,expected,tick,1,
                JsonSerializer.SerializeToElement(state),consumed.Select(RoomControlStore.EventKey).ToArray()));
            return Task.CompletedTask;
        }
    }
    private sealed class NoopGame : IGameModule
    {
        public string Name=>"fencing-test";
        public object GetStateSnapshot()=>new { tick=true };
        public void ApplyEvents(long tickId,IReadOnlyList<AudienceEvent> events) { }
        public void RestoreState(long version,JsonElement state) { }
        public void Dispose() { }
    }
    [Fact]
    public void CheckpointSurvivesDatabaseReopenAndLateOlderTickCannotReplaceIt()
    {
        var s=NewStore();s.ReportNode(Worker("a"));var room=s.Assign(new("room"));
        Assert.True(s.Commit("room",Commit(room)));
        var reopened=NewStore().Room("room",checkpoint:true)!;
        Assert.Equal(1,reopened.TickId);
        Assert.Equal(1,reopened.Snapshot!.Value.GetProperty("count").GetInt32());
        Assert.Equal(SnapshotIntegrity.Compute(reopened.Snapshot.Value.GetRawText()),reopened.Checksum);

        Assert.Throws<ControlConflictException>(()=>s.Commit("room",Commit(room)));
        var stillCurrent=NewStore().Room("room",checkpoint:true)!;
        Assert.Equal(1,stillCurrent.TickId);
        Assert.Equal(1,stillCurrent.Snapshot!.Value.GetProperty("count").GetInt32());
    }
    [Fact]
    public void CorruptCheckpointFailsVisiblyAndUnsupportedSchemaIsRejected()
    {
        var s=NewStore();s.ReportNode(Worker("a"));var room=s.Assign(new("room"));
        var wrongSchema=Commit(room) with { SchemaVersion=2 };
        Assert.Throws<ArgumentException>(()=>s.Commit("room",wrongSchema));
        Assert.Equal(0,s.Room("room",checkpoint:true)!.TickId);
        s.Commit("room",Commit(room));

        using(var db=new SqliteConnection($"Data Source={Path.Combine(_dir,"control.db")}"))
        {
            db.Open();using var command=db.CreateCommand();
            command.CommandText="UPDATE checkpoints SET body='not-json' WHERE room_id='room'";
            Assert.Equal(1,command.ExecuteNonQuery());
        }
        Assert.ThrowsAny<JsonException>(()=>NewStore().Room("room",checkpoint:true));
    }
    [Fact]
    public void FailedRecoveryAbortBumpsEpochAndCannotResurrectStaleActor()
    {
        var s=NewStore();s.ReportNode(Worker("a"));s.ReportNode(Worker("b"));var room=s.Assign(new("room"));
        s.Move("room",new("b",1),"admin");s.Handoff("room",1);var aborted=s.Abort("room",2,"admin");
        Assert.Equal(3,aborted.Epoch);Assert.Equal("a",aborted.WorkerId);Assert.Equal(RoomLifecycleState.Recovering,aborted.State);
        Assert.Throws<ControlConflictException>(()=>s.Prepare("room",new("a","a-boot",room.Epoch)));
    }
    [Fact]
    public void LeaseExpiryFencesOldWriterWithoutAssumingProcessStopped()
    {
        var s=NewStore();s.ReportNode(Worker("a"));var room=s.Assign(new("room"));
        _clock.Advance(RoomControlStore.NodeLeaseMs);
        Assert.Throws<ControlConflictException>(()=>s.Commit("room",Commit(room)));
        s.ReportNode(Worker("a") with { InstanceId="new-boot" });
        Assert.Throws<ControlConflictException>(()=>s.Prepare("room",new("a","a-boot",1)));
    }
    [Fact]
    public void DurableDedupAndConnectionGenerationSurviveRetryAndLateDisconnect()
    {
        var s=NewStore();s.ReportNode(Worker("a"));s.ReportNode(new("g","boot","gateway-host","gateway",new("localhost",4000),0));
        Assert.True(s.AppendEvent(Event()));Assert.False(NewStore().AppendEvent(Event()));
        var old=new ConnectionRecord("room","client","user","streamer","g","boot","old");
        var current=old with { Generation="new" };s.Connect(old);s.Connect(current);s.Disconnect(old);
        Assert.Single(s.Connections());Assert.Equal("new",s.Connections().Single().Generation);
        Assert.Equal(1,s.Summary().OnlineUsers);
        _clock.Advance(RoomControlStore.NodeLeaseMs);Assert.Empty(s.Connections());
    }
    [Fact]
    public void LifecycleCannotDiscardPendingInputOrResurrectDestroyedRoom()
    {
        var s=NewStore();s.ReportNode(Worker("a"));s.Assign(new("room"));s.AppendEvent(Event());
        Assert.Throws<ControlConflictException>(()=>s.Action("room",new(RoomLifecycleAction.Close,1),"admin"));
        s.Commit("room",Commit(s.Room("room")!,0,"a",RoomControlStore.EventKey(Event())));
        var closing=s.Action("room",new(RoomLifecycleAction.Close,1),"admin");
        var destroyed=s.Action("room",new(RoomLifecycleAction.Destroyed,closing.Epoch),"admin");
        Assert.Equal(RoomLifecycleState.Destroyed,s.Assign(new("room")).State);
        Assert.Throws<ControlConflictException>(()=>s.AppendEvent(Event("late")));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if(Directory.Exists(_dir))Directory.Delete(_dir,true); }
    private sealed class FakeClock:TimeProvider
    {
        private long _now=1_800_000_000_000;
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.FromUnixTimeMilliseconds(_now);
        public void Advance(long ms)=>_now+=ms;
    }
}
