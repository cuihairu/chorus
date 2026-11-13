using Chorus.Adapters.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Dispatches verification to the named verifier registered for the platform.
/// Platforms without a registered verifier fall back to the development stub
/// (if one is configured) — the stub answers only the test/simulator fixtures
/// (review 2026-11-13 M3), so a mapper registered without a verifier fails closed
/// instead of sliding through unsigned. With no fallback at all, requests fail closed.
/// </summary>
public sealed class PlatformEventVerifierRouter : IPlatformEventVerifier, IPlatformTrustedAppIdSource
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
            // A duplicate named registration is a wiring error, not an ordering choice
            // (review 2026-11-13 M6): last-wins would silently replace one implementation
            // — possibly the verifying one — with whichever registered later.
            if (!named.TryAdd(verifier.PlatformName, verifier))
            {
                throw new InvalidOperationException(
                    $"Platform '{verifier.PlatformName}' has more than one registered verifier " +
                    $"('{named[verifier.PlatformName].GetType().Name}' and '{verifier.GetType().Name}'): " +
                    "duplicate named verifier registrations are refused; remove one of the two registrations.");
            }
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

    /// <summary>The trusted app identity of whichever verifier answered this platform —
    /// the named one when it claims an identity, else the fallback's (a development
    /// fallback claims none). Null when nothing can be claimed.</summary>
    public string? GetTrustedAppId(string platform)
    {
        if (!string.IsNullOrWhiteSpace(platform) && _named.TryGetValue(platform, out var verifier)
            && verifier is IPlatformTrustedAppIdSource source)
        {
            return source.GetTrustedAppId(platform);
        }

        return (_fallback as IPlatformTrustedAppIdSource)?.GetTrustedAppId(platform);
    }
}
