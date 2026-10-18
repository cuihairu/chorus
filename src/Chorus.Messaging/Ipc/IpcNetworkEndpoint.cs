namespace Chorus.Messaging.Ipc;

/// <summary>Advertised node address and optional local bind address.</summary>
public sealed record IpcNetworkEndpoint(string Host, int Port, string? HttpUrl = null, string? ListenHost = null);
