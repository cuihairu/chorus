namespace Chorus.Protocol;

/// <summary>
/// Wire-protocol version policy for the streamer-ws face. Shared by every host
/// so the single-process and split forms reject exactly the same hellos.
/// Absent version = legacy client (accepted, kept compatible); a version that
/// differs from <see cref="Current"/> is rejected before authentication.
/// </summary>
public static class ProtocolVersionPolicy
{
    /// <summary>Current wire-protocol version. Bump on breaking wire changes (see docs/reference/versioning.md).</summary>
    public const int Current = 1;

    /// <summary>
    /// Returns the rejection reason for the client's hello protocol version,
    /// or null when the hello is acceptable. Absent (null) versions pass:
    /// hard-requiring the field would break pre-versioning clients.
    /// </summary>
    public static string? CheckRejectionReason(int? clientProtocolVersion)
    {
        if (clientProtocolVersion is null || clientProtocolVersion.Value == Current)
        {
            return null;
        }

        return $"protocol_mismatch: client={clientProtocolVersion.Value} server={Current}";
    }
}
