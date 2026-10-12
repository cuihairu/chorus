namespace Chorus.Gateway;

/// <summary>
/// Which listener roles one gateway process hosts (docs/design/process-topology.md
/// §4 three-process shape): All (default — one process, both roles), Platform
/// (platform-gw) or Streamer (streamer-gw). Packaging only: both listener
/// faces are the shared implementations, the gate just omits the other
/// role's endpoint mapping.
/// </summary>
public enum GatewayRoleSet
{
    All,
    Platform,
    Streamer,
}

public static class GatewayRoles
{
    public static GatewayRoleSet Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return GatewayRoleSet.All;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "all" => GatewayRoleSet.All,
            "platform" => GatewayRoleSet.Platform,
            "streamer" => GatewayRoleSet.Streamer,
            _ => throw new InvalidOperationException("Gateway:Roles must be one of: all, platform, streamer"),
        };
    }
}
