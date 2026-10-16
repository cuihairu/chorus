using Chorus.Messaging.Ipc;
using Chorus.Messaging.Transports;

namespace Chorus.Engine.Tests;

/// <summary>
/// Per-gateway IPC channel naming (stage-2 transport half, exposed by the
/// three-process e2e): with several gateways against one worker, each gateway
/// must host its own eng-to-gw endpoint, so the engine can fan out to all of
/// them. An empty GatewayId keeps the legacy single-gateway names byte-for-byte
/// (both ends restart together, but unchanged defaults avoid deploy churn).
/// </summary>
public sealed class IpcEndpointBuilderTests
{
    private static IpcSettings Settings(string gatewayId = "", string workerId = "") =>
        new(Transport: IpcTransport.UnixDomainSocket, GatewayId: gatewayId, WorkerId: workerId);

    [Fact]
    public void EmptyGatewayId_KeepsLegacyNames()
    {
        var eps = IpcEndpointBuilder.Build(Settings());

        Assert.EndsWith("chorus_gw_to_eng.sock", eps.GatewayToEngine.UnixSocketPath, StringComparison.Ordinal);
        Assert.EndsWith("chorus_eng_to_gw.sock", eps.EngineToGateway.UnixSocketPath, StringComparison.Ordinal);
        Assert.Equal("chorus.eng_to_gw", eps.EngineToGateway.NamedPipeName);
    }

    [Fact]
    public void GatewayId_SuffixesOnlyEngineToGateway()
    {
        var eps = IpcEndpointBuilder.Build(Settings("streamer-gw"));

        // Commands channel is shared: every gateway dials the same engine server.
        Assert.EndsWith("chorus_gw_to_eng.sock", eps.GatewayToEngine.UnixSocketPath, StringComparison.Ordinal);
        Assert.EndsWith("chorus_eng_to_gw_streamer-gw.sock", eps.EngineToGateway.UnixSocketPath, StringComparison.Ordinal);
        Assert.Equal("chorus.eng_to_gw.streamer-gw", eps.EngineToGateway.NamedPipeName);
    }

    [Fact]
    public void WorkerId_SuffixesOnlyGatewayToEngine()
    {
        var eps = IpcEndpointBuilder.Build(Settings(workerId: "worker-a"));

        Assert.EndsWith("chorus_gw_to_eng_worker-a.sock", eps.GatewayToEngine.UnixSocketPath, StringComparison.Ordinal);
        Assert.EndsWith("chorus_eng_to_gw.sock", eps.EngineToGateway.UnixSocketPath, StringComparison.Ordinal);
        Assert.Equal("chorus.gw_to_eng.worker-a", eps.GatewayToEngine.NamedPipeName);
    }

    [Theory]
    [InlineData("bad_id")]
    [InlineData("bad/slash")]
    [InlineData("bad.dot")]
    [InlineData("bad space")]
    public void InvalidWorkerId_FailsFast(string workerId)
    {
        Assert.Throws<ArgumentException>(() => IpcEndpointBuilder.Build(Settings(workerId: workerId)));
    }

    [Theory]
    [InlineData("bad_id")]
    [InlineData("bad/slash")]
    [InlineData("bad.dot")]
    [InlineData("bad space")]
    public void InvalidGatewayId_FailsFast(string gatewayId)
    {
        Assert.Throws<ArgumentException>(() => IpcEndpointBuilder.Build(Settings(gatewayId)));
    }
}
