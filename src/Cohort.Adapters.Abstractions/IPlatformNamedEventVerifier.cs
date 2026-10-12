namespace Cohort.Adapters.Abstractions;

/// <summary>
/// A platform event verifier bound to a single platform name.
/// The ingress router dispatches by <see cref="PlatformName"/> and fails closed
/// when a platform has no registered verifier.
/// </summary>
public interface IPlatformNamedEventVerifier : IPlatformEventVerifier
{
    string PlatformName { get; }
}
