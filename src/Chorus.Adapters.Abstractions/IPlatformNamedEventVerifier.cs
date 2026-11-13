namespace Chorus.Adapters.Abstractions;

/// <summary>
/// A platform event verifier bound to a single platform name.
/// The ingress router dispatches by <see cref="PlatformName"/>; a platform with a
/// registered named verifier ALWAYS verifies through it (the AllowAll development
/// switch never weakens a registered platform). A platform without one falls to the
/// development fallback — the AllowAll stub, which answers only the test/simulator
/// fixtures (review 2026-11-13 M3) — and fails closed when no fallback is configured.
/// </summary>
public interface IPlatformNamedEventVerifier : IPlatformEventVerifier
{
    string PlatformName { get; }
}
