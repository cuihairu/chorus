using System.Collections.Concurrent;
using Chorus.Control;
using Chorus.Data;
using Chorus.Engine.Routing;
using Chorus.Engine.Session;
using Chorus.Messaging.Ipc;

namespace Chorus.EngineHost;

public sealed partial class EngineIpcService
{
    private ControlClient? _control;
    private readonly ConcurrentDictionary<string,RoomRecord> _controlledRooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string SessionId,string ClientId),GatewayConnectCmd> _clientOrigins = new();
    private readonly SemaphoreSlim _roomGate = new(1,1);
    private readonly CancellationTokenSource _controlStop = new();
    private Task _controlTask = Task.CompletedTask;
    private string _machineId = Environment.MachineName;

    private async Task ReportControlAsync(CancellationToken ct)
    {
        var endpoint = _ipcSettings.Workers is not null && _ipcSettings.Workers.TryGetValue(_control!.NodeId,out var e) ? e
            : new IpcNetworkEndpoint(_ipcSettings.TcpHost,_ipcSettings.TcpGatewayToEnginePort,_ipcSettings.HttpGatewayToEngineUrl);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        await _control!.Post<NodeReport>("nodes/report",new NodeReport(_control.NodeId,_control.InstanceId,_machineId,"worker",endpoint,
            _sessionConfig.MaxSessions,MemoryBytes:process.WorkingSet64,Version:typeof(EngineIpcService).Assembly.GetName().Version?.ToString() ?? "",Sessions:GetDiagnostics()),ct);
    }
    private async Task<SessionActor> ResolveSessionAsync(string id, CancellationToken ct)
    {
        if (_control is null) return GetOrCreateSession(id);
        var room = await _control.Room(id,ct,checkpoint:true);
        if (room.WorkerId != _control.NodeId || room.WorkerInstance != _control.InstanceId
            || room.State is not (RoomLifecycleState.Waiting or RoomLifecycleState.Active or RoomLifecycleState.Idle or RoomLifecycleState.Recovering))
            throw new ControlConflictException("Room is not assigned to this worker.");
        await _roomGate.WaitAsync(ct);
        try
        {
            if (_controlledRooms.TryGetValue(id,out var previous)
                && (previous.Epoch != room.Epoch || _registry.TryGet(id,out var oldActor) && oldActor!.DurabilityFailed))
            {
                await _registry.CloseAsync(id);
                _controlledRooms.TryRemove(id,out _);
            }
            _controlledRooms[id] = room;
            var actor = _registry.GetOrCreate(id);
            if (room.State == RoomLifecycleState.Recovering)
                await _control.Post<RoomRecord>($"rooms/{Uri.EscapeDataString(id)}/ready",new OwnerRequest(_control.NodeId,_control.InstanceId,room.Epoch),ct);
            return actor;
        }
        finally { _roomGate.Release(); }
    }
    private async Task RunControlAsync(CancellationToken serviceToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceToken,_controlStop.Token);
        var ct = linked.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReportControlAsync(ct);
                var rooms = await _control!.Get<List<RoomRecord>>("rooms",ct);
                var connections = await _control.Get<List<ConnectionRecord>>("connections",ct);
                var mine = rooms.Where(r => r.WorkerId == _control.NodeId && r.WorkerInstance == _control.InstanceId
                    && r.State is RoomLifecycleState.Waiting or RoomLifecycleState.Active or RoomLifecycleState.Idle or RoomLifecycleState.Recovering).ToArray();
                foreach (var obsolete in _controlledRooms.Keys.Where(id => !mine.Any(r => r.Id == id)))
                {
                    await _roomGate.WaitAsync(ct);
                    try { await _registry.CloseAsync(obsolete); _controlledRooms.TryRemove(obsolete,out _); }
                    finally { _roomGate.Release(); }
                }
                foreach (var room in mine)
                {
                    try
                    {
                        var actor = await ResolveSessionAsync(room.Id,ct);
                        var desired = connections.Where(c => c.SessionId == room.Id).ToArray();
                        foreach (var stale in _clients.Keys.Where(k => k.SessionId == room.Id && !desired.Any(c => c.ClientId == k.ClientId)).ToArray())
                        { await actor.RemoveClientAsync(stale.ClientId); _clients.TryRemove(stale,out _); _clientOrigins.TryRemove(stale,out _); _broadcastOwners.OnDisconnect(room.Id,stale.ClientId,desired.Select(c=>c.ClientId)); }
                        foreach (var c in desired)
                        {
                            var key = (room.Id,c.ClientId);
                            if (_clientOrigins.TryGetValue(key,out var current) && current.Generation == c.Generation
                                && _clients.ContainsKey(key) && _controlledRooms[room.Id].Epoch == room.Epoch && actor.Diagnostics.Clients.Any(d => d.ClientId == c.ClientId)) continue;
                            _broadcastOwners.OnConnect(room.Id,c.ClientId);
                            var client = new EngineBusSessionClient(_outgoing!,room.Id,c.ClientId,()=>_broadcastOwners.IsOwner(room.Id,c.ClientId),RecordPublishFailure,room.Epoch);
                            _clients[key] = client;
                            _clientOrigins[key] = new(room.Id,c.ClientId,c.GatewayId,c.GatewayInstance,c.Generation);
                            await actor.AddClientAsync(client);
                        }
                        if (desired.Length == 0) _clientCountBySession.TryRemove(room.Id,out _); else _clientCountBySession[room.Id] = desired.Length;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex,"Controlled room {RoomId} not ready",room.Id); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex,"Control unavailable; ticks cannot commit"); }
            if (!await timer.WaitForNextTickAsync(ct)) break;
        }
    }
}
