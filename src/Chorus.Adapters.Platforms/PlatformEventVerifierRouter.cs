using Chorus.Adapters.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Dispatches verification to the named verifier registered for the platform.
/// Platforms without a registered verifier fall back to the development stub
/// (if one is configured); with no fallback at all, requests fail closed.
/// </summary>
public sealed class PlatformEventVerifierRouter : IPlatformEventVerifier
{
    private readonly IReadOnlyDictionary<string, IPlatformEventVerifier> _named;
    private readonly IPlatformEventVerifier? _fallback;
    private readonly ILogger<PlatformEventVerifierRouter>? _logger;

    public PlatformEventVerifierRouter(
        IEnumerable<IPlatformNamedEventVerifier> namedVerifiers,
        IPlatformEventVerifier? fallback = null,
        ILogger<PlatformEventVerifierRouter>? logger = null)
    {
        var named = new Dictionary<string, IPlatformEventVerifier>(StringComparer.OrdinalIgnoreCase);
        foreach (var verifier in namedVerifiers)
        {
            named[verifier.PlatformName] = verifier;
        }

        _named = named;
        _fallback = fallback;
        _logger = logger;
    }

    public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (!string.IsNullOrWhiteSpace(platform) && _named.TryGetValue(platform, out var verifier))
        {
            return verifier.Verify(platform, rawBody, headers);
        }

        if (_fallback != null)
        {
            return _fallback.Verify(platform, rawBody, headers);
        }

        _logger?.LogWarning(
            "Ingress rejected platform {Platform}: no verifier registered and no fallback configured",
            platform);
        return false;
    }
}
