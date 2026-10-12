using Microsoft.Extensions.Configuration;

namespace Cohort.Gateway.Core.Faces;

/// <summary>
/// Policy for the streamer-ws listener face (streamer / live-companion
/// WebSocket clients). Deliberately independent from the platform-ingress face
/// so one face being flooded does not change the other face's behavior.
///
/// AUTH FAILS CLOSED: with no tokens configured and anonymous access off,
/// every WS connection is rejected. Local demos enable
/// <c>Faces:StreamerWs:AllowAnonymous</c> explicitly in
/// appsettings.Development.json.
/// </summary>
public sealed class StreamerWsFaceOptions
{
    /// <summary>Max size of one WS text message in bytes. Larger messages close the connection.</summary>
    public int MaxMessageBytes { get; init; } = 64 * 1024;

    /// <summary>Max ACK messages per connection per one-minute fixed window. Excess ACKs are dropped.</summary>
    public int AckPerMinute { get; init; } = 600;

    /// <summary>Accept connections whose hello carries no token (dev/demo only; warns loudly when on).</summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>Valid hello tokens parsed from <c>Faces:StreamerWs:AuthTokens</c>.</summary>
    public IReadOnlyList<WsTokenSpec> Tokens { get; init; } = Array.Empty<WsTokenSpec>();

    public static StreamerWsFaceOptions FromConfiguration(IConfiguration configuration)
    {
        return new StreamerWsFaceOptions
        {
            MaxMessageBytes = configuration.GetValue("Faces:StreamerWs:MaxMessageBytes", 64 * 1024),
            AckPerMinute = Math.Max(1, configuration.GetValue("Faces:StreamerWs:AckPerMinute", 600)),
            AllowAnonymous = configuration.GetValue("Faces:StreamerWs:AllowAnonymous", false),
            Tokens = ParseTokens(configuration["Faces:StreamerWs:AuthTokens"]),
        };
    }

    /// <summary>Validates the hello token. Returns the reason string on failure (missing_token / invalid_token / token_expired).</summary>
    public WsAuthResult Authenticate(string? token, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return AllowAnonymous
                ? WsAuthResult.Pass
                : new WsAuthResult(false, "missing_token");
        }

        foreach (var spec in Tokens)
        {
            if (!string.Equals(spec.Value, token, StringComparison.Ordinal))
            {
                continue;
            }

            if (spec.ExpiresAtMs is { } expiry && nowMs >= expiry)
            {
                return new WsAuthResult(false, "token_expired");
            }

            return WsAuthResult.Pass;
        }

        return new WsAuthResult(false, "invalid_token");
    }

    /// <summary>
    /// Parses "token" or "token@unixMsExpiry" entries separated by ';'.
    /// A token value must not itself contain '@' or ';' (the first '@' starts
    /// the optional expiry suffix; entries without a numeric expiry are taken
    /// verbatim as the token).
    /// </summary>
    private static IReadOnlyList<WsTokenSpec> ParseTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<WsTokenSpec>();
        }

        var specs = new List<WsTokenSpec>();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = entry.IndexOf('@');
            if (at > 0 && at < entry.Length - 1 && long.TryParse(entry[(at + 1)..], out var expiry) && expiry > 0)
            {
                specs.Add(new WsTokenSpec(entry[..at], expiry));
            }
            else
            {
                specs.Add(new WsTokenSpec(entry, null));
            }
        }

        return specs;
    }
}

/// <summary>A single valid streamer WS token with an optional unix-ms expiry.</summary>
public sealed record WsTokenSpec(string Value, long? ExpiresAtMs);

/// <summary>Result of streamer WS hello-token validation.</summary>
public sealed record WsAuthResult(bool Ok, string Reason)
{
    public static WsAuthResult Pass { get; } = new(true, "ok");
}
