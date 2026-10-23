using Chorus.Engine.Routing;

namespace Chorus.Control;

public sealed partial class RoomControlStore
{
    /// <summary>Atomically reassigns an expired/replaced process; a healthy owner is never preempted.</summary>
    public RoomRecord RecoverStaleRoom(string id, long expectedEpoch, string actor) => database.Write(t =>
    {
        var room = t.GetForUpdate<RoomRecord>("room", id) ?? throw new KeyNotFoundException(id);
        if (room.Epoch != expectedEpoch
            || room.State is not (RoomLifecycleState.Waiting or RoomLifecycleState.Active or RoomLifecycleState.Idle or RoomLifecycleState.Recovering)
            || Owner(t, room, new(room.WorkerId, room.WorkerInstance, room.Epoch)))
            throw new ControlConflictException("Recovery requires a stale owner and current epoch.");
        var target = SelectWorker(t, replacingRoom: id);
        var recovered = room with
        {
            WorkerId = target.Id, WorkerInstance = target.InstanceId, Epoch = room.Epoch + 1,
            State = RoomLifecycleState.Recovering,
            ResumeState = room.State == RoomLifecycleState.Recovering ? room.ResumeState : room.State,
            PreviousWorker = room.WorkerId, TargetWorker = null, MigrationId = null, UpdatedAtMs = Now
        };
        t.Put("room", id, recovered);
        t.Audit(Now, actor, "room.owner.recover", id, new { previous = room.WorkerId, recovered.WorkerId, recovered.Epoch });
        return recovered;
    });
}
