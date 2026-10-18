using System.Collections.Concurrent;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;

namespace Chorus.Control;

/// <summary>Subscription-directed output with independent per-node transport and timeout.</summary>
public sealed class ControlGatewayBus(ControlClient control, IpcTransport transport, IpcSettings settings) : IMessageBus
{
    private readonly ConcurrentDictionary<string,IMessageBus> _channels = new(StringComparer.Ordinal);
    public async ValueTask PublishAsync(Envelope env, CancellationToken cancellationToken)
    {
        var nodes = await control.Get<List<NodeReport>>("nodes",cancellationToken);
        HashSet<string>? recipients = null;
        if (env.Type != IpcMessageTypes.EngineHeartbeat)
        {
            var room = await control.Room(env.SessionId,cancellationToken);
            if (room.Epoch != env.AuthorityEpoch || room.WorkerId != control.NodeId || room.WorkerInstance != control.InstanceId
                || room.State is not (Chorus.Engine.Routing.RoomLifecycleState.Active or Chorus.Engine.Routing.RoomLifecycleState.Waiting or Chorus.Engine.Routing.RoomLifecycleState.Idle)) return;
            var connections = await control.Get<List<ConnectionRecord>>("connections",cancellationToken);
            recipients = connections.Where(c => c.SessionId == env.SessionId).Select(c => c.GatewayId).ToHashSet(StringComparer.Ordinal);
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var targets = nodes.Where(n => n.Role == "gateway" && now - n.LastHeartbeatMs < RoomControlStore.NodeLeaseMs && (recipients is null || recipients.Contains(n.Id))).ToArray();
        await Task.WhenAll(targets.Select(async node =>
        {
            var key = $"{node.Id}/{node.InstanceId}";
            var bus = _channels.GetOrAdd(key, _ => IpcMessageBusFactory.CreateClient(transport,
                IpcEndpointBuilder.Build(settings with { GatewayId = node.Id, Gateways = new Dictionary<string,IpcNetworkEndpoint> { [node.Id] = node.Endpoint } }).EngineToGateway));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await bus.PublishAsync(env,timeout.Token);
        }));
        foreach (var stale in _channels.Keys.Where(k => !nodes.Any(n => $"{n.Id}/{n.InstanceId}" == k)).ToArray())
            if (_channels.TryRemove(stale,out var bus)) await bus.DisposeAsync();
    }
    public IAsyncEnumerable<Envelope> SubscribeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async ValueTask DisposeAsync() { foreach (var bus in _channels.Values) await bus.DisposeAsync(); _channels.Clear(); }
}
