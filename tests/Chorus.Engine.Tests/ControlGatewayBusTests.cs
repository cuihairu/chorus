using Chorus.Control;
using Chorus.Engine.Routing;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Microsoft.Extensions.Configuration;

namespace Chorus.Engine.Tests;

public sealed class ControlGatewayBusTests
{
    [Fact]
    public async Task ReplacedRoutingSnapshotRejectsOldEpochAndPublishesOnlyToNewGateway()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Control:Url"] = "http://localhost/",
            ["Control:Key"] = "test",
        }).Build();
        using var control = new ControlClient(config, "worker");
        var buses = new Dictionary<string, RecordingBus>(StringComparer.Ordinal);
        await using var output = new ControlGatewayBus(control, IpcTransport.UnixDomainSocket,
            new IpcSettings(), node => buses[node.Id] = new RecordingBus());

        output.ReplaceRoutingState(Routing(now, control.InstanceId, epoch: 4, gatewayId: "gateway-old"));
        output.ReplaceRoutingState(Routing(now, control.InstanceId, epoch: 5, gatewayId: "gateway-new"));

        var stale = new Envelope(IpcMessageTypes.EngineSnapshot, "stale", "room", now, default, AuthorityEpoch: 4);
        await output.PublishAsync(stale, CancellationToken.None);
        var current = stale with { MessageId = "current", AuthorityEpoch = 5 };
        await output.PublishAsync(current, CancellationToken.None);

        Assert.DoesNotContain("gateway-old", buses.Keys);
        Assert.Equal(new[] { "current" }, buses["gateway-new"].Messages.Select(m => m.MessageId));
    }

    private static ControlRoutingData Routing(long now, string workerInstance, long epoch, string gatewayId)
    {
        var nodes = new[]
        {
            new NodeReport("worker", workerInstance, "worker-host", "worker", new("127.0.0.1", 3101), 100, now),
            new NodeReport(gatewayId, gatewayId + "-boot", gatewayId + "-host", "gateway", new("127.0.0.1", 4101), 0, now),
        };
        var room = new RoomRecord("room", "worker", workerInstance, epoch, RoomLifecycleState.Active, now);
        var connection = new ConnectionRecord("room", "client", "user", "streamer", gatewayId, gatewayId + "-boot", "generation", now);
        return new ControlRoutingData(now, nodes, [room], [connection]);
    }

    private sealed class RecordingBus : IMessageBus
    {
        public List<Envelope> Messages { get; } = [];
        public ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            Messages.Add(envelope);
            return ValueTask.CompletedTask;
        }
        public IAsyncEnumerable<Envelope> SubscribeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
