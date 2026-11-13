using Chorus.Adapters.Abstractions;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Development verifier bound to explicit development identities: accepts unsigned
/// events for the test/simulator platforms only — the fixtures a development fleet
/// drives. Mounted as the router fallback by <c>Ingress:AllowAll</c>. The bound
/// identity is deliberate (review 2026-11-13 M3): a mapper registered without a real
/// verifier no longer slides through unsigned, and a registered platform whose
/// signature fails never reaches the fallback anyway. Production hosts refuse the
/// switch outright — each host checks its environment at startup.
/// </summary>
public sealed class AllowAllPlatformEventVerifier : IPlatformEventVerifier
{
    public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers) =>
        string.Equals(platform, PlatformNames.Test, StringComparison.OrdinalIgnoreCase)
        || string.Equals(platform, PlatformNames.Simulator, StringComparison.OrdinalIgnoreCase);
}
