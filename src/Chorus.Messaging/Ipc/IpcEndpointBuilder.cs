namespace Chorus.Messaging.Ipc;

public static class IpcEndpointBuilder
{
    public static IpcDuplexEndpoints Build(IpcSettings settings, bool forServer = false)
    {
        var dir = string.IsNullOrWhiteSpace(settings.UnixSocketDir) ? "/tmp/chorus" : settings.UnixSocketDir;
        var prefix = string.IsNullOrWhiteSpace(settings.NamedPipePrefix) ? "chorus" : settings.NamedPipePrefix;
        var tcpHost = string.IsNullOrWhiteSpace(settings.TcpHost) ? "127.0.0.1" : settings.TcpHost;

        var workerId = ValidatedWorkerId(settings.WorkerId);
        var workerSocketSuffix = workerId.Length == 0 ? "" : $"_{workerId}";
        var workerPipeSuffix = workerId.Length == 0 ? "" : $".{workerId}";
        var gwToEng = new IpcEndpoint(
            UnixSocketPath: Path.Combine(dir, $"{prefix}_gw_to_eng{workerSocketSuffix}.sock"),
            NamedPipeName: $"{prefix}.gw_to_eng{workerPipeSuffix}",
            TcpHost: tcpHost,
            TcpPort: settings.TcpGatewayToEnginePort,
            HttpUrl: NormalizeHttpUrl(settings.HttpGatewayToEngineUrl, "http://127.0.0.1:27600/gw-to-eng/")
        );

        // The eng-to-gw channel is per gateway: each gateway hosts its own
        // endpoint (suffixed by GatewayId) so the worker can fan out to all of
        // them. Empty id keeps the legacy single-gateway names.
        var id = ValidatedGatewayId(settings.GatewayId);
        var socketSuffix = id.Length == 0 ? "" : $"_{id}";
        var pipeSuffix = id.Length == 0 ? "" : $".{id}";
        var shardIndex = ValidatedEngineToGatewayShard(settings, settings.EngineToGatewayShardIndex, settings.EngineToGatewayShards);
        // Shards split one eng-to-gw channel into several sockets, one per
        // session-hash bucket. Count 1 keeps today's names byte-for-byte.
        var shardSocketSuffix = shardIndex < 0 ? "" : $"_{shardIndex}";
        var shardPipeSuffix = shardIndex < 0 ? "" : $".{shardIndex}";
        var engToGw = new IpcEndpoint(
            UnixSocketPath: Path.Combine(dir, $"{prefix}_eng_to_gw{socketSuffix}{shardSocketSuffix}.sock"),
            NamedPipeName: $"{prefix}.eng_to_gw{pipeSuffix}{shardPipeSuffix}",
            TcpHost: tcpHost,
            TcpPort: settings.TcpEngineToGatewayPort,
            HttpUrl: NormalizeHttpUrl(settings.HttpEngineToGatewayUrl, "http://127.0.0.1:27601/eng-to-gw/")
        );

        gwToEng = ApplyNetworkEndpoint(gwToEng, settings.Workers, workerId, forServer);
        engToGw = ApplyNetworkEndpoint(engToGw, settings.Gateways, id, forServer);
        return new IpcDuplexEndpoints(gwToEng, engToGw);
    }

    private static IpcEndpoint ApplyNetworkEndpoint(IpcEndpoint fallback,
        IReadOnlyDictionary<string, IpcNetworkEndpoint>? nodes, string id, bool forServer)
    {
        if (nodes is null || nodes.Count == 0 || id.Length == 0) return fallback;
        if (!nodes.TryGetValue(id, out var node))
            throw new ArgumentException($"Missing IPC endpoint for node '{id}'.");
        if (string.IsNullOrWhiteSpace(node.Host) || node.Host is "0.0.0.0" or "::" or "*"
            || node.Port is < 1 or > 65535)
            throw new ArgumentException($"Invalid advertised endpoint for node '{id}'.");
        if (nodes.Any(pair => pair.Key != id && pair.Value.Host == node.Host && pair.Value.Port == node.Port))
            throw new ArgumentException($"Duplicate IPC endpoint for node '{id}'.");
        if (node.HttpUrl is not null && (!Uri.TryCreate(node.HttpUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException($"Invalid HTTP endpoint for node '{id}'.");
        return fallback with
        {
            TcpHost = forServer ? node.ListenHost ?? node.Host : node.Host,
            TcpPort = node.Port,
            HttpUrl = node.HttpUrl is null ? fallback.HttpUrl : NormalizeHttpUrl(node.HttpUrl, fallback.HttpUrl),
        };
    }

    private static string ValidatedGatewayId(string gatewayId)
        => ValidateEndpointId(gatewayId, nameof(IpcSettings.GatewayId));

    /// <summary>Validates the eng-to-gw shard split. Returns the validated
    /// shard index, or -1 when sharding is off (count 1 keeps the legacy
    /// unsuffixed endpoint names). A non-positive count or an index outside
    /// the range fails fast: both ends must agree on the same shard split, and
    /// silently reverting to the legacy channel would just read as a
    /// performance surprise.</summary>
    private static int ValidatedEngineToGatewayShard(IpcSettings settings, int shardIndex, int shards)
    {
        if (shards < 1)
        {
            throw new ArgumentException(
                $"Ipc {nameof(IpcSettings.EngineToGatewayShards)} must be >= 1: {shards}",
                nameof(IpcSettings.EngineToGatewayShards));
        }

        if (shardIndex < 0 || shardIndex >= shards)
        {
            throw new ArgumentException(
                $"Ipc {nameof(IpcSettings.EngineToGatewayShardIndex)} must be within [0, {shards}) for {nameof(IpcSettings.EngineToGatewayShards)}={shards}: {shardIndex}",
                nameof(IpcSettings.EngineToGatewayShardIndex));
        }

        if (shards == 1)
        {
            return -1;
        }

        if (settings.Transport is IpcTransport.Tcp or IpcTransport.Http)
        {
            throw new ArgumentException(
                $"Ipc {nameof(IpcSettings.EngineToGatewayShards)} > 1 requires a socket transport; {settings.Transport} advertises one endpoint per gateway id.",
                nameof(IpcSettings.EngineToGatewayShards));
        }

        return shardIndex;
    }

    private static string ValidatedWorkerId(string workerId)
        => ValidateEndpointId(workerId, nameof(IpcSettings.WorkerId));

    private static string ValidateEndpointId(string id, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "";
        }

        id = id.Trim();
        foreach (var ch in id)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
            {
                throw new ArgumentException(
                    $"Ipc {propertyName} must contain only ASCII letters, digits and dashes: '{id}'",
                    propertyName);
            }
        }

        return id;
    }

    private static string NormalizeHttpUrl(string? url, string fallback)
    {
        url = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim();
        return url.EndsWith('/') ? url : $"{url}/";
    }
}
