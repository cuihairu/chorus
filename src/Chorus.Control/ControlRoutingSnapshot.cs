using Chorus.Engine.Routing;

namespace Chorus.Control;

/// <summary>Immutable routing view used on the high-frequency IPC output path.</summary>
public sealed class ControlRoutingSnapshot
{
    public const long MaxAgeMs = 1_500;
    private readonly IReadOnlyDictionary<string, NodeReport> _nodes;
    private readonly IReadOnlyDictionary<string, RoomRecord> _rooms;
    private readonly IReadOnlyDictionary<string, string[]> _gatewaysByRoom;
    public long CreatedAtMs { get; }

    public ControlRoutingSnapshot(long createdAtMs, IEnumerable<NodeReport> nodes,
        IEnumerable<RoomRecord> rooms, IEnumerable<ConnectionRecord> connections)
    {
        CreatedAtMs = createdAtMs;
        _nodes = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _rooms = rooms.ToDictionary(r => r.Id, StringComparer.Ordinal);
        _gatewaysByRoom = connections.GroupBy(c => c.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.GatewayId).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    public bool IsFresh(long nowMs) => nowMs >= CreatedAtMs && nowMs - CreatedAtMs < MaxAgeMs;

    public bool TryGetTargets(string roomId, long? epoch, string workerId, string workerInstance,
        long nowMs, out IReadOnlyList<NodeReport> targets)
    {
        targets = Array.Empty<NodeReport>();
        if (!TryGetAuthority(roomId, epoch, nowMs, out var room) || room.WorkerId != workerId
            || room.WorkerInstance != workerInstance || !LiveWorker(room, nowMs)
            || !_gatewaysByRoom.TryGetValue(roomId, out var gatewayIds)) return false;

        var live = gatewayIds.Where(id => _nodes.TryGetValue(id, out var node) && node.Role == "gateway"
                && !node.Draining && nowMs >= node.LastHeartbeatMs
                && nowMs - node.LastHeartbeatMs < RoomControlStore.NodeLeaseMs)
            .Select(id => _nodes[id]).ToArray();
        if (live.Length == 0) return false;
        targets = live;
        return true;
    }

    public bool TryGetAuthority(string roomId, long? epoch, long nowMs, out RoomRecord room)
    {
        room = null!;
        if (!IsFresh(nowMs) || epoch is null || !_rooms.TryGetValue(roomId, out var found)
            || found.Epoch != epoch
            || found.State is not (RoomLifecycleState.Active or RoomLifecycleState.Waiting or RoomLifecycleState.Idle)
            || !LiveWorker(found,nowMs)) return false;
        room = found;
        return true;
    }

    private bool LiveWorker(RoomRecord room,long nowMs) => _nodes.TryGetValue(room.WorkerId,out var worker)
        && worker.Role == "worker" && worker.InstanceId == room.WorkerInstance && nowMs >= worker.LastHeartbeatMs
        && nowMs - worker.LastHeartbeatMs < RoomControlStore.NodeLeaseMs;

    public IReadOnlyList<NodeReport> LiveGateways(long nowMs) => !IsFresh(nowMs) ? Array.Empty<NodeReport>()
        : _nodes.Values.Where(n => n.Role == "gateway" && !n.Draining && nowMs >= n.LastHeartbeatMs
            && nowMs - n.LastHeartbeatMs < RoomControlStore.NodeLeaseMs).ToArray();
}
