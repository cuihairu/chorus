namespace Chorus.Messaging.Ipc;

public sealed record GatewayConnectCmd(string SessionId, string ClientId);

public sealed record GatewayDisconnectCmd(string SessionId, string ClientId);

public sealed record GatewayAckCmd(string SessionId, string ClientId, long LastAppliedTickId, long ClientTimeMs);

