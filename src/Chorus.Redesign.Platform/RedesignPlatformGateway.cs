namespace Chorus.Redesign.Platform;

/// <summary>
/// Routes launch verification to the adapter selected by platform name. Adapters without
/// production evidence are gated behind <paramref name="allowNotProductionReady"/> so a
/// development simulator can never silently serve a production host.
/// </summary>
public sealed class RedesignPlatformGateway(IEnumerable<IPlatformLaunchAdapter> adapters, bool allowNotProductionReady = false)
{
    private readonly Dictionary<string, IPlatformLaunchAdapter> _adapters =
        adapters.ToDictionary(adapter => adapter.Platform, StringComparer.Ordinal);

    public async Task<VerifiedLaunch> VerifyLaunchAsync(string platform, PlatformLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(platform))
            throw new PlatformLaunchRejectedException("platform-unknown", "Platform is required.");
        if (!_adapters.TryGetValue(platform, out var adapter))
            throw new PlatformLaunchRejectedException("platform-unknown", $"No launch adapter is registered for '{platform}'.");
        if (!adapter.Capabilities.ProductionReady && !allowNotProductionReady)
            throw new PlatformLaunchRejectedException("adapter-not-production-ready",
                $"The '{platform}' adapter is not cleared for production use.");
        var verified = await adapter.VerifyLaunchAsync(request, cancellationToken);
        if (!StringComparer.Ordinal.Equals(verified.Platform, platform) ||
            !StringComparer.Ordinal.Equals(verified.AppId, request.AppId) ||
            string.IsNullOrWhiteSpace(verified.OpenId) ||
            string.IsNullOrWhiteSpace(verified.CanonicalLiveSessionKey))
            throw new PlatformLaunchRejectedException("adapter-violation", "The launch adapter returned an inconsistent verification.");
        return verified;
    }
}
