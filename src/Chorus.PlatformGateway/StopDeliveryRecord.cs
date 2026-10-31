namespace Chorus.PlatformGateway;

/// <summary>
/// The gateway's record of the stop-delivery commands it closed at (FIX-END-05): each room's
/// end operation id and the exact CloseRouteVersion it accepted. The gRPC service is created
/// per request, so this record is the process-wide truth the handler reads and writes — a
/// repeated command at the recorded version answers duplicate, and a command naming a
/// different version for a room the gateway already closed is refused, so a late replay can
/// never close a newer round. In-memory by design: the control plane's persisted command and
/// responder set are the durable facts; this record only makes one gateway's ack idempotent.
/// </summary>
public sealed class StopDeliveryRecord
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string EndOperationId, long CloseRouteVersion)> _closedAt = new();

    /// <summary>True when the room's closure now stands at exactly this version — a first
    /// command records it, a replay at the recorded version confirms it. False when the room
    /// already closed at a different version: nothing is written, and the recorded version is
    /// the one that refused the command.</summary>
    public bool TryClose(string roomId, string endOperationId, long closeRouteVersion, out bool duplicate,
        out long recordedVersion)
    {
        lock (_gate)
        {
            if (_closedAt.TryGetValue(roomId, out var closed))
            {
                duplicate = true;
                recordedVersion = closed.CloseRouteVersion;
                if (closed.CloseRouteVersion != closeRouteVersion) return false; // stale: refused
                return true;
            }
            _closedAt[roomId] = (endOperationId, closeRouteVersion);
            duplicate = false;
            recordedVersion = closeRouteVersion;
            return true;
        }
    }
}
