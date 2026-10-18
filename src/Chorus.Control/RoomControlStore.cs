using System.Text.Json;
using Chorus.Data;
using Chorus.Engine.Routing;

namespace Chorus.Control;

/// <summary>Durable room directory, fenced checkpoints and bounded reliable input.</summary>
public sealed partial class RoomControlStore(ControlDatabase database, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    public const long NodeLeaseMs = 15000;
    public const long ConnectionLeaseMs = 30000;
    public const int MaxInbox = 8000;
    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();
    private static void Id(string id) { ArgumentException.ThrowIfNullOrWhiteSpace(id); if (id.Length > 200) throw new ArgumentException("Identifier too long."); }
    private bool Healthy(NodeReport? node) => node is not null && Now - node.LastHeartbeatMs < NodeLeaseMs;
    private static bool Live(RoomRecord room) => room.State != RoomLifecycleState.Destroyed;
    public List<NodeReport> Nodes() => database.Read(t => t.List<NodeReport>("node"));
    public List<RoomRecord> Rooms() => database.Read(t => t.List<RoomRecord>("room"));
    public ControlRoutingData RoutingData() => database.Read(t => new ControlRoutingData(Now,
        t.List<NodeReport>("node"),t.List<RoomRecord>("room"),Connections(t)));
    public RoomRecord? Room(string id, bool checkpoint = false) => database.Read(t =>
    {
        var r=t.Get<RoomRecord>("room", id); if(r is null) return null;
        var cp=t.GetCheckpoint(id);
        return r with { Snapshot=checkpoint ? cp?.State : null, TickId=cp?.Tick ?? 0, Checksum=cp?.Checksum ?? 0, SchemaVersion=cp?.Schema ?? 1 };
    });
    public int PendingCount(string id) => database.Read(t=>t.PendingCount(id));
    public NodeReport ReportNode(NodeReport report) => database.Write(t =>
    {
        Id(report.Id); Id(report.InstanceId); Id(report.MachineId);
        if (report.Role is not ("worker" or "gateway") || report.Capacity < 0) throw new ArgumentException("Invalid node role/capacity.");
        var old = t.Get<NodeReport>("node", report.Id);
        if (Healthy(old) && old!.InstanceId != report.InstanceId) throw new ControlConflictException("Node identity already leased by another process.");
        var result = report with { LastHeartbeatMs = Now, Draining = old?.Draining ?? false };
        t.Put("node", report.Id, result);
        return result;
    });
    public NodeReport Drain(string id, bool draining, string actor) => database.Write(t =>
    {
        var node = t.Get<NodeReport>("node", id) ?? throw new KeyNotFoundException(id);
        node = node with { Draining = draining };
        t.Put("node", id, node); t.Audit(Now, actor, "node.drain", id, new { draining });
        return node;
    });
    private NodeReport SelectWorker(ControlDatabase.Transaction t, string? target = null)
    {
        var rooms = t.List<RoomRecord>("room");
        return t.List<NodeReport>("node").Where(n => n.Role == "worker" && Healthy(n) && !n.Draining && (target is null || n.Id == target))
            .Select(n => (Node: n, Count: rooms.Count(r => Live(r) && r.WorkerId == n.Id)))
            .Where(x => x.Node.Capacity == 0 || x.Count < x.Node.Capacity)
            .OrderBy(x => x.Count).ThenBy(x => x.Node.Id, StringComparer.Ordinal).Select(x => x.Node).FirstOrDefault()
            ?? throw new ControlConflictException("No healthy worker with available capacity.");
    }
    private RoomRecord Assign(ControlDatabase.Transaction t, RoomAssignment request)
    {
        Id(request.Id);
        var old = t.Get<RoomRecord>("room", request.Id);
        if (old is not null) return old; // Closed ids are tombstones, never implicitly resurrected.
        var worker = SelectWorker(t);
        var room = new RoomRecord(request.Id, worker.Id, worker.InstanceId, 1, RoomLifecycleState.Waiting, Now,
            request.StreamerId, request.Platform, request.Mode, request.Region);
        t.Put("room", room.Id, room);
        t.Audit(Now, "system", "room.assign", room.Id, new { worker.Id, room.Epoch });
        return room;
    }
    public RoomRecord Assign(RoomAssignment request) => database.Write(t => Assign(t, request));
    private bool Owner(ControlDatabase.Transaction t, RoomRecord r, OwnerRequest owner)
    {
        var node = t.Get<NodeReport>("node", owner.WorkerId);
        return r.WorkerId == owner.WorkerId && r.WorkerInstance == owner.InstanceId && r.Epoch == owner.Epoch
            && Healthy(node) && node!.InstanceId == owner.InstanceId;
    }
    public RoomRecord Prepare(string id, OwnerRequest owner) => database.Read(t =>
    {
        var r = t.Get<RoomRecord>("room", id) ?? throw new KeyNotFoundException(id);
        if (!Owner(t, r, owner) || r.State is not (RoomLifecycleState.Waiting or RoomLifecycleState.Active or RoomLifecycleState.Idle))
            throw new ControlConflictException("Room is not writable by this owner/epoch.");
        return r with { Inbox=t.PendingEvents(id,Math.Clamp(MaxInbox,1,MaxInbox)) };
    });
    public RoomRecord Ready(string id, OwnerRequest owner) => database.Write(t =>
    {
        var r = t.Get<RoomRecord>("room", id) ?? throw new KeyNotFoundException(id);
        if (!Owner(t, r, owner) || r.State != RoomLifecycleState.Recovering) throw new ControlConflictException("Recovery owner changed.");
        r = r with { State = r.ResumeState is RoomLifecycleState.Waiting or RoomLifecycleState.Idle ? r.ResumeState.Value : RoomLifecycleState.Active, UpdatedAtMs = Now, MigrationId = null, TargetWorker = null };
        t.Put("room", id, r); t.Audit(Now, owner.WorkerId, "room.recovered", id, new { r.Epoch, r.TickId });
        return r;
    });
    public bool Commit(string id, TickCommit commit) => database.Write(t =>
    {
        var r = t.Get<RoomRecord>("room", id) ?? throw new KeyNotFoundException(id);
        if (!Owner(t, r, new(commit.WorkerId, commit.InstanceId, commit.Epoch))
            || r.State is not (RoomLifecycleState.Waiting or RoomLifecycleState.Active or RoomLifecycleState.Idle)
            || r.TickId != commit.ExpectedTick || commit.TickId != commit.ExpectedTick + 1)
            throw new ControlConflictException("Checkpoint fenced: stale owner, state or tick.");
        if (commit.SchemaVersion != 1 || commit.State.GetRawText().Length > 1_048_576) throw new ArgumentException("Invalid checkpoint schema/size.");
        var keys = new HashSet<string>(commit.ConsumedEventKeys, StringComparer.Ordinal);
        if (keys.Any(key => !t.ReceiptExists(id,key))) throw new ControlConflictException("Checkpoint contains unknown input receipts.");
        t.DeleteEvents(id,keys.ToArray());
        var checksum=SnapshotIntegrity.Compute(commit.State.GetRawText());
        t.PutCheckpoint(id,commit.State,commit.TickId,commit.Epoch,checksum,commit.SchemaVersion);
        r = r with { TickId = commit.TickId, Snapshot = null, Checksum = checksum,
            SchemaVersion = commit.SchemaVersion, Inbox = null, State = keys.Count > 0 ? RoomLifecycleState.Active : r.State };
        t.Put("room", id, r);
        return true;
    });
    public static string EventKey(Chorus.Protocol.Models.AudienceEvent e) => JsonSerializer.Serialize(new[] { e.Platform, e.EventId });
    public bool AppendEvent(Chorus.Protocol.Models.AudienceEvent e) => database.Write(t =>
    {
        Id(e.EventId); Id(e.UserId);
        var r = Assign(t, new(e.SessionId, Platform: e.Platform));
        if (r.State is RoomLifecycleState.Closing or RoomLifecycleState.Destroyed) throw new ControlConflictException("Room is closed.");
        var key = EventKey(e);
        t.PruneReceipts(r.Id,Now-86_400_000);
        if(t.ReceiptExists(r.Id,key)) return false;
        if(t.PendingCount(r.Id)>=MaxInbox) throw new ControlConflictException("Reliable room inbox is full; retry later.");
        t.AddEvent(r.Id,key,e,Now);
        r = r with { UpdatedAtMs = Now };
        t.Put("room", r.Id, r);
        return true; // SQLite transaction is committed before HTTP success.
    });
    public RoomRecord Action(string id, RoomAction request, string actor) => database.Write(t =>
    {
        var r = t.Get<RoomRecord>("room", id) ?? throw new KeyNotFoundException(id);
        if (r.Epoch != request.ExpectedEpoch || !RoomLifecycleTransitions.CanTransition(r.State, request.Action, out var state))
            throw new ControlConflictException("Stale epoch or illegal lifecycle transition.");
        if (request.Action is RoomLifecycleAction.BeginMigration or RoomLifecycleAction.BeginRecovery or RoomLifecycleAction.Recovered)
            throw new ControlConflictException("Use the migration/recovery protocol.");
        if (request.Action is RoomLifecycleAction.Close or RoomLifecycleAction.Destroyed && t.PendingCount(id) > 0)
            throw new ControlConflictException("Drain reliable input before closing.");
        if (request.Action == RoomLifecycleAction.Sleep && Connections(t).Any(c => c.SessionId == id))
            throw new ControlConflictException("Connected room cannot sleep.");
        r = r with { State = state, Epoch = r.Epoch + 1, UpdatedAtMs = Now, ResumeState = r.State };
        t.Put("room", id, r); t.Audit(Now, actor, "room.lifecycle", id, request);
        return r;
    });
    public RoomRecord Move(string id, RoomMove request, string actor) => database.Write(t =>
    {
        var r = t.Get<RoomRecord>("room", id) ?? throw new KeyNotFoundException(id);
        if (r.Epoch != request.ExpectedEpoch || r.State is not (RoomLifecycleState.Active or RoomLifecycleState.Waiting or RoomLifecycleState.Idle))
            throw new ControlConflictException("Room cannot begin migration.");
        var target = SelectWorker(t, request.TargetWorker);
        if (target.Id == r.WorkerId && target.InstanceId == r.WorkerInstance) throw new ControlConflictException("Target already owns room.");
        r = r with { State = RoomLifecycleState.Migrating, TargetWorker = target.Id, PreviousWorker = r.WorkerId,
            MigrationId = Guid.NewGuid().ToString("N"), ResumeState = r.State, UpdatedAtMs = Now };
        t.Put("room", id, r); t.Audit(Now, actor, "room.migrate.begin", id, request);
        return r;
    });
    public RoomRecord Handoff(string id, long epoch) => database.Write(t =>
    {
        var r = t.Get<RoomRecord>("room", id) ?? throw new KeyNotFoundException(id);
        if (r.Epoch != epoch || r.State != RoomLifecycleState.Migrating) throw new ControlConflictException("Migration no longer current.");
        var target = SelectWorker(t, r.TargetWorker);
        r = r with { WorkerId = target.Id, WorkerInstance = target.InstanceId, Epoch = r.Epoch + 1, State = RoomLifecycleState.Recovering, UpdatedAtMs = Now };
        t.Put("room", id, r); t.Audit(Now, "system", "room.migrate.handoff", id, new { r.Epoch, r.WorkerId });
        return r;
    });
    public RoomRecord Abort(string id, long epoch, string actor) => database.Write(t =>
    {
        var r = t.Get<RoomRecord>("room", id) ?? throw new KeyNotFoundException(id);
        if (r.Epoch != epoch || r.State is not (RoomLifecycleState.Migrating or RoomLifecycleState.Recovering)) throw new ControlConflictException("No current migration to abort.");
        var previous = t.Get<NodeReport>("node", r.PreviousWorker ?? r.WorkerId);
        if (!Healthy(previous)) throw new ControlConflictException("Previous worker unavailable; preserve recovery state.");
        r = r with { WorkerId = previous!.Id, WorkerInstance = previous.InstanceId, Epoch = r.Epoch + 1, State = RoomLifecycleState.Recovering,
            UpdatedAtMs = Now, TargetWorker = null, MigrationId = null };
        t.Put("room", id, r); t.Audit(Now, actor, "room.migrate.abort", id, new { r.Epoch });
        return r;
    });
    public void Connect(ConnectionRecord connection) => database.Write(t =>
    {
        var gateway = t.Get<NodeReport>("node", connection.GatewayId);
        if (!Healthy(gateway) || gateway!.InstanceId != connection.GatewayInstance) throw new ControlConflictException("Unknown gateway lease.");
        var r = Assign(t, new(connection.SessionId, connection.Role == "streamer" ? connection.UserId : null));
        if (r.State is RoomLifecycleState.Closing or RoomLifecycleState.Destroyed) throw new ControlConflictException("Room closed.");
        if (connection.Role == "streamer" && r.StreamerId is null) t.Put("room", r.Id, r with { StreamerId = connection.UserId });
        t.Put("connection", ConnectionKey(connection), connection with { LastSeenMs = Now });
        return true;
    });
    private static string ConnectionKey(ConnectionRecord c) => JsonSerializer.Serialize(new[] { c.SessionId, c.ClientId });
    public void Disconnect(ConnectionRecord connection) => database.Write(t =>
    {
        var key = ConnectionKey(connection);
        var current = t.Get<ConnectionRecord>("connection", key);
        if (current?.Generation == connection.Generation && current.GatewayInstance == connection.GatewayInstance) t.Delete("connection", key);
        return true;
    });
    private List<ConnectionRecord> Connections(ControlDatabase.Transaction t) => t.List<ConnectionRecord>("connection")
        .Where(c => { var n = t.Get<NodeReport>("node", c.GatewayId); return Healthy(n) && n!.InstanceId == c.GatewayInstance && Now - c.LastSeenMs < ConnectionLeaseMs; }).ToList();
    public List<ConnectionRecord> Connections() => database.Read(Connections);
    public void RenewConnections(string gatewayId, string instance, IReadOnlyList<ConnectionRecord> connections)
    {
        foreach (var c in connections) if (c.GatewayId != gatewayId || c.GatewayInstance != instance) throw new ArgumentException("Gateway connection identity mismatch.");
        database.Write(t =>
        {
            var node = t.Get<NodeReport>("node", gatewayId);
            if (!Healthy(node) || node!.InstanceId != instance) throw new ControlConflictException("Gateway lease expired.");
            foreach (var c in connections)
            {
                var key = ConnectionKey(c); var old = t.Get<ConnectionRecord>("connection", key);
                if (old?.Generation == c.Generation && old.GatewayInstance == instance) t.Put("connection", key, old with { LastSeenMs = Now });
            }
            foreach (var old in t.List<ConnectionRecord>("connection").Where(c => Now - c.LastSeenMs >= ConnectionLeaseMs)) t.Delete("connection", ConnectionKey(old));
            return true;
        });
    }
    public SummarySample Summary() => database.Read(t =>
    {
        var nodes = t.List<NodeReport>("node").Where(Healthy).ToArray();
        var rooms = t.List<RoomRecord>("room").Where(Live).ToArray();
        var connections = Connections(t);
        return new SummarySample(Now, nodes.Select(n => n.MachineId).Distinct().Count(), nodes.Count(n => n.Role == "worker"), nodes.Count(n => n.Role == "gateway"),
            rooms.Length, rooms.Count(r => r.State == RoomLifecycleState.Active), connections.Count, connections.Select(c => c.UserId).Distinct().Count(),
            connections.Where(c => c.Role == "streamer").Select(c => c.UserId).Distinct().Count(), t.List<QueueEntry>("queue").Count,
            t.List<MatchRecord>("match").Count(m => m.Status == "Running"));
    });
    public List<AuditRecord> Audits(int limit = 100) => database.Read(t => t.Audits(limit));
    public void Sample()
    {
        var summary = Summary();
        database.Write(t => { var history = t.Get<List<SummarySample>>("system", "history") ?? []; history.Add(summary); if (history.Count > 720) history.RemoveRange(0, history.Count - 720); t.Put("system", "history", history); return true; });
    }
    public List<SummarySample> History() => database.Read(t => t.Get<List<SummarySample>>("system", "history") ?? []);
}
