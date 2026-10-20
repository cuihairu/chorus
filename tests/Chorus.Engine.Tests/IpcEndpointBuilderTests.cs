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
    private static IpcSettings Settings(string gatewayId = "", string workerId = "",
        int engToGwShards = 1, int engToGwShardIndex = 0) =>
        new(Transport: IpcTransport.UnixDomainSocket, GatewayId: gatewayId, WorkerId: workerId,
            EngineToGatewayShards: engToGwShards, EngineToGatewayShardIndex: engToGwShardIndex);

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

    [Fact]
    public void SingleShard_KeepsLegacyNamesByteForByte()
    {
        // Shards=1 (default) must produce today's names exactly: existing
        // deployments keep their sockets across the upgrade.
        var eps = IpcEndpointBuilder.Build(Settings("streamer-gw"));

        Assert.EndsWith("chorus_eng_to_gw_streamer-gw.sock", eps.EngineToGateway.UnixSocketPath, StringComparison.Ordinal);
        Assert.Equal("chorus.eng_to_gw.streamer-gw", eps.EngineToGateway.NamedPipeName);
    }

    [Theory]
    [InlineData(0, "chorus_eng_to_gw_platform-gw_0.sock", "chorus.eng_to_gw.platform-gw.0")]
    [InlineData(1, "chorus_eng_to_gw_platform-gw_1.sock", "chorus.eng_to_gw.platform-gw.1")]
    [InlineData(2, "chorus_eng_to_gw_platform-gw_2.sock", "chorus.eng_to_gw.platform-gw.2")]
    public void ShardedEngineToGateway_SuffixesShardIndex(int shardIndex, string socketSuffix, string pipeName)
    {
        var eps = IpcEndpointBuilder.Build(Settings("platform-gw", engToGwShards: 3, engToGwShardIndex: shardIndex));

        Assert.EndsWith(socketSuffix, eps.EngineToGateway.UnixSocketPath, StringComparison.Ordinal);
        Assert.Equal(pipeName, eps.EngineToGateway.NamedPipeName);
        // Commands channel stays single: shards only split engine→gateway.
        Assert.EndsWith("chorus_gw_to_eng.sock", eps.GatewayToEngine.UnixSocketPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ShardedEngineToGateway_EmptyGatewayId_StillSuffixesShard()
    {
        var eps = IpcEndpointBuilder.Build(Settings(engToGwShards: 2, engToGwShardIndex: 1));

        Assert.EndsWith("chorus_eng_to_gw_1.sock", eps.EngineToGateway.UnixSocketPath, StringComparison.Ordinal);
        Assert.Equal("chorus.eng_to_gw.1", eps.EngineToGateway.NamedPipeName);
    }

    [Theory]
    [InlineData(0, 0)]   // shard count must be >= 1
    [InlineData(2, 2)]   // index out of range
    [InlineData(2, -1)]  // negative index
    [InlineData(1, 1)]   // single shard has no index 1
    public void InvalidShardConfig_FailsFast(int shards, int shardIndex)
    {
        Assert.Throws<ArgumentException>(() =>
            IpcEndpointBuilder.Build(Settings(engToGwShards: shards, engToGwShardIndex: shardIndex)));
    }

    [Fact]
    public void ShardedEngineToGateway_TcpTransport_FailsFast()
    {
        // TCP sharding needs per-shard advertised ports (Gateways map is keyed
        // by gateway id only) — refuse instead of binding N shards to one port.
        var settings = Settings("platform-gw", engToGwShards: 2, engToGwShardIndex: 0)
            with { Transport = IpcTransport.Tcp };
        Assert.Throws<ArgumentException>(() => IpcEndpointBuilder.Build(settings));
    }
}
