namespace Chorus.Messaging.Ipc;

public static class IpcEndpointBuilder
{
    public static IpcDuplexEndpoints Build(IpcSettings settings)
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
        var engToGw = new IpcEndpoint(
            UnixSocketPath: Path.Combine(dir, $"{prefix}_eng_to_gw{socketSuffix}.sock"),
            NamedPipeName: $"{prefix}.eng_to_gw{pipeSuffix}",
            TcpHost: tcpHost,
            TcpPort: settings.TcpEngineToGatewayPort,
            HttpUrl: NormalizeHttpUrl(settings.HttpEngineToGatewayUrl, "http://127.0.0.1:27601/eng-to-gw/")
        );

        return new IpcDuplexEndpoints(gwToEng, engToGw);
    }

    private static string ValidatedGatewayId(string gatewayId)
        => ValidateEndpointId(gatewayId, nameof(IpcSettings.GatewayId));

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
