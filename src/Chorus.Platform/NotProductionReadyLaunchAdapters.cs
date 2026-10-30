namespace Chorus.Platform;

/// <summary>
/// Base for named platform adapters that must fail closed until their production admission
/// evidence exists: official protocol version, sanitized real samples, signature test cases,
/// and success receipt format. They never verify a credential.
/// </summary>
public abstract class NotProductionReadyLaunchAdapter : IPlatformLaunchAdapter
{
    protected NotProductionReadyLaunchAdapter(string platform, string missingEvidence)
    {
        Platform = platform;
        Capabilities = new PlatformLaunchCapabilities(platform, ProductionReady: false,
            Evidence: $"not-production-ready: missing {missingEvidence}");
    }

    public string Platform { get; }
    public PlatformLaunchCapabilities Capabilities { get; }

    public Task<VerifiedLaunch> VerifyLaunchAsync(PlatformLaunchRequest request, CancellationToken cancellationToken = default)
        => throw new PlatformLaunchRejectedException("adapter-not-production-ready",
            $"{Platform} launch verification is not production ready; no credential is accepted.");
}

/// <summary>Douyin live gameplay adapter; rejects all launches until production evidence exists.</summary>
public sealed class DouyinLaunchAdapter() : NotProductionReadyLaunchAdapter("douyin",
    "official live gameplay protocol, sanitized real samples, and signature test cases");

/// <summary>Kuaishou live gameplay adapter; rejects all launches until production evidence exists.</summary>
public sealed class KuaishouLaunchAdapter() : NotProductionReadyLaunchAdapter("kuaishou",
    "official live gameplay protocol, sanitized real samples, and signature test cases");
