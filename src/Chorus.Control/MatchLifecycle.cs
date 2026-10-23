using Chorus.Engine.Routing;

namespace Chorus.Control;

public sealed partial class RoomControlStore
{
    /// <summary>Reconciles admission and cleanup in separate, retryable match transactions.</summary>
    public void MaintainMatches()
    {
        foreach (var match in Matches().Where(m => !m.RoomsRetired && m.Status is "AwaitingReady" or "Finished" or "Cancelled"))
            database.Write(t =>
            {
                var current = t.GetForUpdate<MatchRecord>("match", match.Id)!;
                if (current.Status == "AwaitingReady")
                {
                    if (Now >= (current.ReadyDeadlineMs ?? current.CreatedAtMs + 120_000))
                        CancelMatch(t, current, "system");
                    else if (!current.FilledWithBots && current.RoomIds.Count == 1)
                    {
                        var room = t.Get<RoomRecord>("room", current.RoomIds[0]);
                        var present = Connections(t).Where(c => c.SessionId == room?.Id && c.Role == "streamer")
                            .Select(c => c.UserId).ToHashSet(StringComparer.Ordinal);
                        if (room is not null && !room.RetirementRequested && room.TickId > 0
                            && room.State is RoomLifecycleState.Waiting or RoomLifecycleState.Active or RoomLifecycleState.Idle
                            && Owner(t, room, new(room.WorkerId, room.WorkerInstance, room.Epoch))
                            && current.StreamerIds.All(present.Contains))
                        {
                            t.Put("match", current.Id, current with { Status = "Running" });
                            t.Audit(Now, "system", "match.started", current.Id, new { room.Id, room.Epoch });
                        }
                    }
                }
                else if (current.Status is "Finished" or "Cancelled") RetireMatchRooms(t, current);
                return true;
            });
    }

    public MatchRecord CancelMatch(string id, string actor) => database.Write(t =>
    {
        var match = t.GetForUpdate<MatchRecord>("match", id) ?? throw new KeyNotFoundException(id);
        if (match.Status == "Cancelled") return match;
        if (match.Status != "AwaitingReady") throw new ControlConflictException("Only a pending match can be cancelled.");
        return CancelMatch(t, match, actor);
    });

    private MatchRecord CancelMatch(ControlDatabase.Transaction t, MatchRecord match, string actor)
    {
        match = match with { Status = "Cancelled", FinishedAtMs = Now };
        t.Put("match", match.Id, match);
        // Preserve queue age only for participants still online outside the retiring room.
        var online = Connections(t).Where(c => c.Role == "streamer" && !match.RoomIds.Contains(c.SessionId))
            .Select(c => c.UserId).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in match.QueueEntries ?? [])
            if (online.Contains(entry.StreamerId) && t.Get<QueueEntry>("queue", entry.StreamerId) is null)
                t.Put("queue", entry.StreamerId, entry);
        RetireMatchRooms(t, match);
        t.Audit(Now, actor, "match.cancelled", match.Id, new { match.Status });
        return match;
    }

    private void RetireMatchRooms(ControlDatabase.Transaction t, MatchRecord match)
    {
        var connections = Connections(t);
        foreach (var id in match.RoomIds)
        {
            var room = t.GetForUpdate<RoomRecord>("room", id);
            if (room is null || room.MatchId != match.Id || room.State == RoomLifecycleState.Destroyed) continue;
            // Stop new input/connections first. Existing durable input must still drain.
            room = room with { RetirementRequested = true };
            if (t.PendingCount(id) == 0 && !connections.Any(c => c.SessionId == id))
            {
                if (room.State != RoomLifecycleState.Closing)
                {
                    if (!RoomLifecycleTransitions.CanTransition(room.State, RoomLifecycleAction.Close, out var closing)) continue;
                    room = room with { State = closing, Epoch = room.Epoch + 1, UpdatedAtMs = Now };
                    t.Audit(Now, "system", "room.match.closing", id, new { match.Id, room.Epoch });
                }
                room = room with { State = RoomLifecycleState.Destroyed, Epoch = room.Epoch + 1, UpdatedAtMs = Now };
                t.Audit(Now, "system", "room.match.retired", id, new { match.Id, room.Epoch });
            }
            t.Put("room", id, room);
        }
        if (match.RoomIds.All(id => t.Get<RoomRecord>("room", id) is not { State: not RoomLifecycleState.Destroyed }))
            t.Put("match", match.Id, match with { RoomsRetired = true });
    }
}
