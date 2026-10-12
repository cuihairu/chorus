namespace Chorus.Messaging.Ipc;

public sealed record IpcDuplexEndpoints(
    IpcEndpoint GatewayToEngine,
    IpcEndpoint EngineToGateway
);

