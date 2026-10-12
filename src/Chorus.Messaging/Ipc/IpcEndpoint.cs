namespace Chorus.Messaging.Ipc;

public sealed record IpcEndpoint(
    string UnixSocketPath,
    string NamedPipeName,
    string TcpHost = "127.0.0.1",
    int TcpPort = 0,
    string HttpUrl = ""
);
