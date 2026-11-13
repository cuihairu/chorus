namespace Chorus.Adapters.Abstractions;

/// <summary>
/// A verifier that can name the application identity its mounted credentials
/// prove. The ingress binds the caller-supplied <c>X-Chorus-App-Id</c> header
/// to this identity: a header that disagrees is an attribution spoof and is
/// refused instead of re-scoping dedup downstream (platform architecture
/// review C1). Verifiers whose credentials carry no id-form identity do not
/// implement this — the header keeps its pre-existing meaning for those
/// platforms, and no binding is claimed.
/// </summary>
public interface IPlatformTrustedAppIdSource
{
    /// <summary>The app id the platform's mounted credentials prove, or null
    /// when nothing can be claimed (unknown platform, or a credential set
    /// without an id-form identity).</summary>
    string? GetTrustedAppId(string platform);
}
