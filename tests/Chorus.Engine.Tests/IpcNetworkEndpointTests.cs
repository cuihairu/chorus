using Chorus.Messaging.Ipc;
using Chorus.Messaging;
using Chorus.Messaging.Transports;
using System.Text.Json;

namespace Chorus.Engine.Tests;

public sealed class IpcNetworkEndpointTests
{
    [Fact]
    public void DistinctNodesHaveIndependentAddressesAndLocalBindAddress()
    {
        var settings = new IpcSettings(WorkerId:"a",GatewayId:"g",Workers:new Dictionary<string,IpcNetworkEndpoint>
        { ["a"] = new("10.0.0.1",3001,"http://10.0.0.1:4001/","0.0.0.0"), ["b"] = new("10.0.0.2",3002,"http://10.0.0.2:4002/") },
            Gateways:new Dictionary<string,IpcNetworkEndpoint> { ["g"] = new("10.0.0.3",3003,"http://10.0.0.3:4003/") });
        Assert.Equal("10.0.0.1",IpcEndpointBuilder.Build(settings).GatewayToEngine.TcpHost);
        Assert.Equal("0.0.0.0",IpcEndpointBuilder.Build(settings,true).GatewayToEngine.TcpHost);
        Assert.Equal(3002,IpcEndpointBuilder.Build(settings with { WorkerId = "b" }).GatewayToEngine.TcpPort);
        Assert.Equal(3003,IpcEndpointBuilder.Build(settings).EngineToGateway.TcpPort);
        Assert.Equal("http://10.0.0.2:4002/",IpcEndpointBuilder.Build(settings with { WorkerId = "b" }).GatewayToEngine.HttpUrl);
        Assert.Throws<ArgumentException>(()=>IpcEndpointBuilder.Build(settings with { WorkerId = "missing" }));
    }
    [Fact]
    public void DuplicateAddressesAreRejected()
    {
        var nodes = new Dictionary<string,IpcNetworkEndpoint> { ["a"] = new("localhost",3000),["b"] = new("localhost",3000) };
        Assert.Throws<ArgumentException>(()=>IpcEndpointBuilder.Build(new IpcSettings(WorkerId:"a",Workers:nodes)));
    }

    [Fact]
    public async Task IndependentTcpWorkersAndGatewaysExchangeMessagesBothWays()
    {
        await using var workerA = Server("0.0.0.0");
        await using var workerB = Server("0.0.0.0");
        await using var gatewayA = Server("127.0.0.1");
        await using var gatewayB = Server("127.0.0.1");
        var settings = new IpcSettings(Transport: IpcTransport.Tcp,
            Workers: new Dictionary<string,IpcNetworkEndpoint>
            {
                ["worker-a"] = new("localhost",workerA.ListeningPort,ListenHost:"0.0.0.0"),
                ["worker-b"] = new("127.0.0.1",workerB.ListeningPort,ListenHost:"0.0.0.0"),
            },
            Gateways: new Dictionary<string,IpcNetworkEndpoint>
            {
                ["gateway-a"] = new("localhost",gatewayA.ListeningPort,ListenHost:"127.0.0.1"),
                ["gateway-b"] = new("127.0.0.1",gatewayB.ListeningPort,ListenHost:"127.0.0.1"),
            });
        await using var workerAClient = Client(IpcEndpointBuilder.Build(settings with { WorkerId = "worker-a" }).GatewayToEngine);
        await using var workerBClient = Client(IpcEndpointBuilder.Build(settings with { WorkerId = "worker-b" }).GatewayToEngine);
        await using var gatewayAClient = Client(IpcEndpointBuilder.Build(settings with { GatewayId = "gateway-a" }).EngineToGateway);
        await using var gatewayBClient = Client(IpcEndpointBuilder.Build(settings with { GatewayId = "gateway-b" }).EngineToGateway);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var workerARead = First(workerA, timeout.Token);
        var workerBRead = First(workerB, timeout.Token);
        var gatewayARead = First(gatewayA, timeout.Token);
        var gatewayBRead = First(gatewayB, timeout.Token);

        await workerAClient.PublishAsync(Message("to-worker-a"),timeout.Token);
        await workerBClient.PublishAsync(Message("to-worker-b"),timeout.Token);
        await gatewayAClient.PublishAsync(Message("to-gateway-a"),timeout.Token);
        await gatewayBClient.PublishAsync(Message("to-gateway-b"),timeout.Token);

        Assert.Equal("to-worker-a",(await workerARead).MessageId);
        Assert.Equal("to-worker-b",(await workerBRead).MessageId);
        Assert.Equal("to-gateway-a",(await gatewayARead).MessageId);
        Assert.Equal("to-gateway-b",(await gatewayBRead).MessageId);
    }

    private static TcpMessageBus Server(string host)
    {
        var bus = new TcpMessageBus(host,0);
        bus.StartServer();
        return bus;
    }
    private static IMessageBus Client(IpcEndpoint endpoint) => IpcMessageBusFactory.CreateClient(IpcTransport.Tcp,endpoint);
    private static Envelope Message(string id) => new("test",id,"room",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),JsonSerializer.SerializeToElement(new { id }));
    private static async Task<Envelope> First(IMessageBus bus,CancellationToken ct)
    {
        await foreach(var env in bus.SubscribeAsync(ct)) return env;
        throw new InvalidOperationException("No IPC message received.");
    }
}
