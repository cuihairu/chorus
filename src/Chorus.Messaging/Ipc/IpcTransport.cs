namespace Chorus.Messaging.Ipc;

public enum IpcTransport
{
    Auto = 0,
    UnixDomainSocket = 1,
    NamedPipe = 2,
    Tcp = 3,
    Http = 4,
}
