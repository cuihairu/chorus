using Chorus.Engine.Routing;

namespace Chorus.Control;

/// <summary>Immutable routing view used on the high-frequency IPC output path.</summary>
public sealed class ControlRoutingSnapshot
{
    public const long MaxAgeMs = 1_500;
    private readonly IReadOnlyDictionary<string, NodeReport> _nodes;
    private readonly IReadOnlyDictionary<string, RoomRecord> _rooms;
    private readonly IReadOnlyDictionary<string, string[]> _gatewaysByRoom;
    private readonly IReadOnlyList<ConnectionRecord> _connections;
    public long CreatedAtMs { get; }

    public ControlRoutingSnapshot(long createdAtMs, IEnumerable<NodeReport> nodes,
        IEnumerable<RoomRecord> rooms, IEnumerable<ConnectionRecord> connections)
    {
        CreatedAtMs = createdAtMs;
        _nodes = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _rooms = rooms.ToDictionary(r => r.Id, StringComparer.Ordinal);
        _connections = connections.ToArray();
        _gatewaysByRoom = _connections.GroupBy(c => c.SessionId, StringComparer.Ordinal)
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

    public bool TryGetCurrentAuthority(string roomId, long nowMs, out RoomRecord room)
    {
        room = null!;
        if (!IsFresh(nowMs) || !_rooms.TryGetValue(roomId, out var found)
            || found.State is not (RoomLifecycleState.Active or RoomLifecycleState.Waiting or RoomLifecycleState.Idle)
            || !TryGetLiveWorker(found, nowMs, out _)) return false;
        room = found;
        return true;
    }

    public bool TryGetLiveWorker(RoomRecord room, long nowMs, out NodeReport worker)
    {
        worker = null!;
        if (!IsFresh(nowMs) || !_nodes.TryGetValue(room.WorkerId, out var found)
            || found.Role != "worker" || found.InstanceId != room.WorkerInstance
            || nowMs < found.LastHeartbeatMs || nowMs - found.LastHeartbeatMs >= RoomControlStore.NodeLeaseMs)
            return false;
        worker = found;
        return true;
    }

    public bool TryValidateConnect(string roomId,long epoch,string workerId,string workerInstance,
        string clientId,string generation,string gatewayInstance,long nowMs,out RoomRecord room)
    {
        room=null!;
        if(!TryGetAuthority(roomId,epoch,nowMs,out var found) || found.WorkerId!=workerId
            || found.WorkerInstance!=workerInstance) return false;
        var connection=_connections.FirstOrDefault(c=>c.SessionId==roomId && c.ClientId==clientId
            && c.Generation==generation && c.GatewayInstance==gatewayInstance);
        if(connection is null || !_nodes.TryGetValue(connection.GatewayId,out var gateway)
            || gateway.Role!="gateway" || gateway.InstanceId!=gatewayInstance || gateway.Draining
            || nowMs<gateway.LastHeartbeatMs || nowMs-gateway.LastHeartbeatMs>=RoomControlStore.NodeLeaseMs)
            return false;
        room=found;
        return true;
    }

    private bool LiveWorker(RoomRecord room,long nowMs) => TryGetLiveWorker(room, nowMs, out _);

    public IReadOnlyList<NodeReport> LiveGateways(long nowMs) => !IsFresh(nowMs) ? Array.Empty<NodeReport>()
        : _nodes.Values.Where(n => n.Role == "gateway" && !n.Draining && nowMs >= n.LastHeartbeatMs
            && nowMs - n.LastHeartbeatMs < RoomControlStore.NodeLeaseMs).ToArray();
}
