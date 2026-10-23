using System.Collections.Concurrent;
using Chorus.Control;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;

namespace Chorus.Gateway;

public sealed partial class GatewayIpcService
{
    private readonly ProcessResourceSampler _resourceSampler=new(ReadCpuTime,ReadMemoryBytes);
    private static TimeSpan ReadCpuTime(){using var p=System.Diagnostics.Process.GetCurrentProcess();return p.TotalProcessorTime;}
    private static long ReadMemoryBytes(){using var p=System.Diagnostics.Process.GetCurrentProcess();return p.WorkingSet64;}
    private ControlClient? _control;
    private readonly ConcurrentDictionary<string,IMessageBus> _controlChannels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,long> _workerLastSeen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _controlStop = new();
    private Task _controlTask = Task.CompletedTask;
    private volatile bool _controlReady;
    private ControlRoutingSnapshot? _controlRoutingSnapshot;
    private string _machineId = Environment.MachineName;
    private ConnectionRecord Connection(GatewayClientConnection conn) => new(conn.SessionId,conn.ClientId,conn.ClientId,"streamer",_control!.NodeId,_control.InstanceId,conn.Generation);
    private async Task RunControlAsync(CancellationToken serviceToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceToken,_controlStop.Token);
        var ct = linked.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var endpoint = _ipcSettings.Gateways is not null && _ipcSettings.Gateways.TryGetValue(_control!.NodeId,out var e) ? e
                    : new IpcNetworkEndpoint(_ipcSettings.TcpHost,_ipcSettings.TcpEngineToGatewayPort,_ipcSettings.HttpEngineToGatewayUrl);
                var usage=_resourceSampler.Sample();
                await _control!.Post<NodeReport>("nodes/report",new NodeReport(_control.NodeId,_control.InstanceId,_machineId,"gateway",endpoint,0,
                    MemoryBytes:usage.MemoryBytes,CpuPercent:usage.CpuPercent),ct);
                var connections = _clientsBySession.Values.SelectMany(m => m.Values).Select(Connection).ToArray();
                await _control.Send("connections/renew",new { gatewayId = _control.NodeId, instanceId = _control.InstanceId, connections },ct);
                var routing = await _control.Routing(ct);
                var rooms = routing.Rooms;
                foreach (var room in rooms.Where(r => r.RetirementRequested))
                    if (_clientsBySession.TryGetValue(room.Id, out var clients))
                        foreach (var client in clients.Values) client.Abort();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Volatile.Write(ref _controlRoutingSnapshot,new ControlRoutingSnapshot(routing.SnapshotAtMs,routing.Nodes,rooms,Array.Empty<ConnectionRecord>()));
                _controlReady = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _controlReady = false; _logger.LogWarning(ex,"Control discovery unavailable"); }
            if (!await timer.WaitForNextTickAsync(ct)) break;
        }
    }
    private async ValueTask PublishControlledAsync(Envelope env, CancellationToken ct)
    {
        // Platform success means durably persisted, not merely written to IPC.
        if (env.Type == IpcMessageTypes.GatewayAudienceEvent)
        {
            await _control!.Send("events",EnvelopeFactory.DeserializeBody<Chorus.Protocol.Models.AudienceEvent>(env),ct);
            return;
        }
        if (env.Type is IpcMessageTypes.GatewayAck or IpcMessageTypes.GatewayResync)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var snapshot = Volatile.Read(ref _controlRoutingSnapshot);
            if (snapshot is null || !snapshot.TryGetCurrentAuthority(env.SessionId, now, out var cachedRoom)
                || !snapshot.TryGetLiveWorker(cachedRoom, now, out var cachedWorker))
                throw new ControlConflictException("Fresh room routing authority unavailable.");
            await PublishToWorkerAsync(cachedRoom, cachedWorker, env, ct);
            return;
        }
        var room = await _control!.Assign(env.SessionId,ct);
        var nodes = await _control.Get<List<NodeReport>>("nodes",ct);
        var worker = nodes.FirstOrDefault(n => n.Role == "worker" && n.Id == room.WorkerId && n.InstanceId == room.WorkerInstance)
            ?? throw new ControlConflictException("Room owner unavailable.");
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - worker.LastHeartbeatMs >= RoomControlStore.NodeLeaseMs)
            throw new ControlConflictException("Room owner lease expired.");
        await PublishToWorkerAsync(room, worker, env, ct);
    }

    private async ValueTask PublishToWorkerAsync(RoomRecord room, NodeReport worker, Envelope env, CancellationToken ct)
    {
        var key = $"{worker.Id}/{worker.InstanceId}";
        var outgoing = _controlChannels.GetOrAdd(key, _ => IpcMessageBusFactory.CreateClient(_ipcSettings.Transport,
            IpcEndpointBuilder.Build(_ipcSettings with { WorkerId = worker.Id, Workers = new Dictionary<string, IpcNetworkEndpoint> { [worker.Id] = worker.Endpoint } }).GatewayToEngine));
        await SafePublishToAsync(outgoing,env with { AuthorityEpoch = room.Epoch },ct);
    }
}
