namespace Chorus.Messaging.Ipc;

public sealed record GatewayConnectCmd(string SessionId, string ClientId, string? GatewayId = null, string? GatewayInstance = null, string? Generation = null);

public sealed record GatewayDisconnectCmd(string SessionId, string ClientId, string? GatewayId = null, string? GatewayInstance = null, string? Generation = null);

public sealed record GatewayAckCmd(string SessionId, string ClientId, long LastAppliedTickId, long ClientTimeMs);

/// <summary>Client-initiated gap-fill request (frame-sync P2): the gateway relays
/// the last frame the client applied; the engine answers from its frame history.</summary>
public sealed record GatewayResyncCmd(string SessionId, string ClientId, long FromFrameId);


/// <summary>
/// Engine-side liveness ping on the eng→gw channel (todo §8 P3 运维):
/// lets the Gateway's /ready probe distinguish "engine down" from "no
/// sessions yet" even when no client is connected.
/// </summary>
public sealed record EngineHeartbeatCmd(long SentTimeMs, string? WorkerId = null, string? InstanceId = null);
