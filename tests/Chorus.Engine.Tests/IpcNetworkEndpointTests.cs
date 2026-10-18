using Chorus.Messaging.Ipc;

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
}
