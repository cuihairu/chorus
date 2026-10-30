namespace Chorus.Platform;

/// <summary>What a launch adapter can verify, with the evidence that justifies its readiness claim.</summary>
public sealed record PlatformLaunchCapabilities(string Platform, bool ProductionReady, string Evidence);

/// <summary>One launch verification attempt; the credential is platform opaque and never logged or returned.</summary>
public sealed record PlatformLaunchRequest(string AppId, string LaunchCredential, string LoginAttemptId);

/// <summary>Trusted launch identity produced by an adapter; carries no secret material.</summary>
public sealed record VerifiedLaunch(
    string Platform,
    string AppId,
    string OpenId,
    string CanonicalLiveSessionKey,
    string LoginAttemptId,
    DateTimeOffset VerifiedAtUtc);

/// <summary>Fail-closed verification error; codes are stable, messages never include the credential.</summary>
public sealed class PlatformLaunchRejectedException(string errorCode, string message)
    : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Verifies one platform launch credential into a trusted, app-scoped streamer identity and live context,
/// per the redesign contract (PlatformIdentity / PlatformGateway, VerifyLaunch).
/// </summary>
public interface IPlatformLaunchAdapter
{
    string Platform { get; }
    PlatformLaunchCapabilities Capabilities { get; }
    Task<VerifiedLaunch> VerifyLaunchAsync(PlatformLaunchRequest request, CancellationToken cancellationToken = default);
}
