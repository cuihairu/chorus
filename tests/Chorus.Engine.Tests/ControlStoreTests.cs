using System.Text.Json;
using Chorus.Control;
using Chorus.Engine.Routing;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

public sealed class ControlStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),"chorus-control-"+Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private RoomControlStore NewStore() => new(new ControlDatabase(Path.Combine(_dir,"control.db")),_clock);
    private static NodeReport Worker(string id, int capacity = 10) => new(id,id+"-boot","host-"+id,"worker",new("127.0.0.1",3000+(id=="a"?1:2)),capacity);
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
        Assert.Equal("a",NewStore().Room("room")!.WorkerId);
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
    public void FencedMigrationRestoresCommittedStateAndPendingInput()
    {
        var s=NewStore();s.ReportNode(Worker("a"));s.ReportNode(Worker("b"));var room=s.Assign(new("room"));
        s.AppendEvent(Event());s.Commit("room",Commit(room,0,"a",RoomControlStore.EventKey(Event())));
        s.AppendEvent(Event("pending"));
        var moving=s.Move("room",new("b",room.Epoch),"operator");
        Assert.Throws<ControlConflictException>(()=>s.Commit("room",Commit(room,1)));
        var incoming=s.Handoff("room",moving.Epoch);
        Assert.Equal(2,incoming.Epoch);Assert.Equal(1,incoming.TickId);Assert.Single(incoming.Inbox!);
        Assert.Throws<ControlConflictException>(()=>s.Prepare("room",new("b","b-boot",2)));
        s.Ready("room",new("b","b-boot",2));
        Assert.Throws<ControlConflictException>(()=>s.Commit("room",Commit(room,1)));
        Assert.True(s.Commit("room",Commit(incoming,1,"b",RoomControlStore.EventKey(Event("pending")))));
        Assert.Empty(NewStore().Room("room")!.Inbox!);
        Assert.Throws<ControlConflictException>(()=>s.Commit("room",Commit(incoming,1,"b")));
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
