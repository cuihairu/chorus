namespace Chorus.GameServer;

/// <summary>
/// A prepare or recovery names a package this host does not ship (FIX-MODULE-02): the
/// room is refused by name — the message names the missing package and what this host
/// ships — and the caller never receives a room silently rebuilt under the default
/// example module. Maps to FailedPrecondition at the gRPC face.
/// </summary>
public sealed class ModulePackageException(string message) : Exception(message);
