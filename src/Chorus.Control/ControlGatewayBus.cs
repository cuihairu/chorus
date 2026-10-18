using System.Collections.Concurrent;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;

namespace Chorus.Control;

/// <summary>Subscription-directed output with independent per-node transport and timeout.</summary>
public sealed class ControlGatewayBus : IMessageBus
{
    private readonly ConcurrentDictionary<string,IMessageBus> _channels = new(StringComparer.Ordinal);
    private readonly ControlClient control;
    private readonly IpcTransport transport;
    private readonly IpcSettings settings;
    private readonly Func<NodeReport,IMessageBus> _createChannel;
    private ControlRoutingSnapshot? _snapshot;

    public ControlGatewayBus(ControlClient control, IpcTransport transport, IpcSettings settings,
        Func<NodeReport,IMessageBus>? createChannel = null)
    {
        this.control = control;
        this.transport = transport;
        this.settings = settings;
        _createChannel = createChannel ?? (node => IpcMessageBusFactory.CreateClient(transport,
            IpcEndpointBuilder.Build(settings with { GatewayId = node.Id,
                Gateways = new Dictionary<string,IpcNetworkEndpoint> { [node.Id] = node.Endpoint } }).EngineToGateway));
    }

    public void ReplaceRoutingState(ControlRoutingData routing) =>
        Volatile.Write(ref _snapshot,new ControlRoutingSnapshot(routing.SnapshotAtMs,routing.Nodes,routing.Rooms,routing.Connections));

    public async Task RefreshRoutingStateAsync(CancellationToken ct = default)
    {
        ReplaceRoutingState(await control.Routing(ct));
    }

    public bool TryValidateConnect(string roomId,long epoch,string workerId,string workerInstance,
        string clientId,string generation,string gatewayInstance,long nowMs,out RoomRecord room)
    {
        var snapshot=Volatile.Read(ref _snapshot);
        if(snapshot is not null) return snapshot.TryValidateConnect(roomId,epoch,workerId,workerInstance,
            clientId,generation,gatewayInstance,nowMs,out room);
        room=null!;
        return false;
    }

    public async ValueTask PublishAsync(Envelope env, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot=Volatile.Read(ref _snapshot);
        if(snapshot is null) return;
        if(snapshot is null || !snapshot.IsFresh(now)) return;
        IReadOnlyList<NodeReport> targets;
        if(env.Type == IpcMessageTypes.EngineHeartbeat) targets=snapshot.LiveGateways(now);
        else if(!snapshot.TryGetTargets(env.SessionId,env.AuthorityEpoch,control.NodeId,control.InstanceId,now,out targets)) return;
        await Task.WhenAll(targets.Select(async node =>
        {
            var key = $"{node.Id}/{node.InstanceId}";
            var bus = _channels.GetOrAdd(key, _ => _createChannel(node));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await bus.PublishAsync(env,timeout.Token);
        }));
        var live=snapshot.LiveGateways(now).Select(n=>$"{n.Id}/{n.InstanceId}").ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _channels.Keys.Where(k => !live.Contains(k)).ToArray())
            if (_channels.TryRemove(stale,out var bus)) await bus.DisposeAsync();
    }
    public IAsyncEnumerable<Envelope> SubscribeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async ValueTask DisposeAsync() { foreach (var bus in _channels.Values) await bus.DisposeAsync(); _channels.Clear(); }
}
