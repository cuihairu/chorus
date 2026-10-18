namespace Chorus.Messaging.Ipc;

public sealed record IpcSettings(
    IpcTransport Transport = IpcTransport.Auto,
    string UnixSocketDir = "/tmp/chorus",
    string NamedPipePrefix = "chorus",
    string TcpHost = "127.0.0.1",
    int TcpGatewayToEnginePort = 27500,
    int TcpEngineToGatewayPort = 27501,
    string HttpGatewayToEngineUrl = "http://127.0.0.1:27600/gw-to-eng/",
    string HttpEngineToGatewayUrl = "http://127.0.0.1:27601/eng-to-gw/",
    /// <summary>The hosting gateway's own id; suffixes its eng-to-gw endpoint so
    /// several gateways can share one worker (empty = legacy single-gateway name).</summary>
    string GatewayId = "",
    /// <summary>The hosting worker's id; suffixes gw-to-eng so one gateway can
    /// route rooms to several workers (empty = legacy single-worker name).</summary>
    string WorkerId = "",
    IReadOnlyDictionary<string, IpcNetworkEndpoint>? Workers = null,
    IReadOnlyDictionary<string, IpcNetworkEndpoint>? Gateways = null
);
