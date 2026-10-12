using Microsoft.Extensions.Configuration;

namespace Chorus.Gateway.Core.Faces;

/// <summary>
/// Policy for the platform-ingress listener face (Douyin/Kuaishou callbacks and
/// open-platform events). Deliberately independent from the streamer-ws face so
/// one face being flooded does not change the other face's behavior.
///
/// See docs/design/process-topology.md: both faces live in the same process for
/// now (code-layer separation only); physical process split comes later.
/// </summary>
public sealed class IngressFaceOptions
{
    /// <summary>Maximum accepted request body size in bytes. Larger requests are rejected with 413.</summary>
    public long MaxBodyBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum requests per source per one-minute fixed window. Excess requests get 429.</summary>
    public int RateLimitPerMinute { get; init; } = 1200;

    /// <summary>Per-request processing budget in milliseconds (body read + verify + map + ingest). Timed-out requests get 504.</summary>
    public int TimeoutMs { get; init; } = 5000;

    public static IngressFaceOptions FromConfiguration(IConfiguration configuration)
    {
        return new IngressFaceOptions
        {
            MaxBodyBytes = configuration.GetValue("Faces:PlatformIngress:MaxBodyBytes", 64 * 1024),
            RateLimitPerMinute = configuration.GetValue("Faces:PlatformIngress:RateLimitPerMinute", 1200),
            TimeoutMs = configuration.GetValue("Faces:PlatformIngress:TimeoutMs", 5000),
        };
    }
}
