using System.Security.Cryptography.X509Certificates;
using Chorus.Redesign.Rpc;
using Grpc.Net.Client;

namespace Chorus.ControlServer;

/// <summary>
/// The control plane's outbound channel directory (architecture 7.2): pooled, mutually
/// authenticated channels to the data plane and the game hosts, seeded from configuration.
/// Each entry names the host instance and where it serves — "id=endpoint" pairs, or a bare
/// endpoint whose address doubles as the instance id (M3-4 convention). The instance id is
/// what the coordinator writes into ownership and commit fences; the self-reported routing
/// snapshot (M3-5) joins it back to the endpoint the host serves on.
/// </summary>
public sealed class ClusterDirectory(
    X509Certificate2 clientCertificate, X509Certificate2 root, X509Certificate2 nodeAuthority,
    string clusterId, string dataEndpoint, IReadOnlyList<string> gameEndpoints)
{
    public GrpcChannel DataChannel { get; } = ProcessGrpcClientFactory.Create(
        new Uri(dataEndpoint), clientCertificate, root, nodeAuthority, clusterId, "data-server");

    private readonly (string InstanceId, GrpcChannel Channel)[] _gameHosts = gameEndpoints
        .Select(entry =>
        {
            var separator = entry.IndexOf('=');
            // "game-a=https://host:port" names the instance; a bare endpoint is its own id.
            var (instanceId, endpoint) = separator < 0
                ? (entry, entry)
                : (entry[..separator], entry[(separator + 1)..]);
            return (instanceId,
                ProcessGrpcClientFactory.Create(new Uri(endpoint), clientCertificate, root, nodeAuthority,
                    clusterId, "game-server"));
        })
        .ToArray();

    private int _cursor;

    /// <summary>The number of configured game hosts — zero means the coordinator must not pair.</summary>
    public int GameHostCount => _gameHosts.Length;

    /// <summary>The configured host instance ids — the recovery sweep's candidate list
    /// (FIX-TAKE-05): a timed-out recovery moves to one of these, never a caller's word.</summary>
    public IReadOnlyList<string> GameHostIds => _gameHosts.Select(host => host.InstanceId).ToArray();

    /// <summary>Round-robin over the configured hosts: spreading rooms before routing exists.</summary>
    public (string InstanceId, GrpcChannel Channel) NextGameHost()
    {
        if (_gameHosts.Length == 0)
            throw new InvalidOperationException("No game endpoints are configured for the coordinator.");
        return _gameHosts[Interlocked.Increment(ref _cursor) % _gameHosts.Length];
    }

    /// <summary>The channel of one configured host — a recovered room's owner must be reached
    /// where it lives, not wherever round-robin points next. Null when the owner is unknown
    /// to this directory.</summary>
    public GrpcChannel? GameHostChannel(string instanceId)
    {
        foreach (var host in _gameHosts)
            if (host.InstanceId == instanceId)
                return host.Channel;
        return null;
    }
}
