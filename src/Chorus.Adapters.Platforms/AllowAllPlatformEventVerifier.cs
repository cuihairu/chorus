using Chorus.Adapters.Abstractions;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Development stub: accepts every event. Registered as the router fallback so
/// local demos work without platform credentials; production setups must rely
/// on real per-platform verifiers instead (see docs/decisions.md).
/// </summary>
public sealed class AllowAllPlatformEventVerifier : IPlatformEventVerifier
{
    public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers) => true;
}
