namespace Chorus.Messaging.Ipc;

public sealed record GatewayConnectCmd(string SessionId, string ClientId);

public sealed record GatewayDisconnectCmd(string SessionId, string ClientId);

public sealed record GatewayAckCmd(string SessionId, string ClientId, long LastAppliedTickId, long ClientTimeMs);


/// <summary>
/// Engine-side liveness ping on the eng→gw channel (todo §8 P3 运维):
/// lets the Gateway's /ready probe distinguish "engine down" from "no
/// sessions yet" even when no client is connected.
/// </summary>
public sealed record EngineHeartbeatCmd(long SentTimeMs);
