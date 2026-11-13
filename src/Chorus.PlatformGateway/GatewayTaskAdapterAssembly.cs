using Chorus.Platform;

namespace Chorus.PlatformGateway;

/// <summary>
/// The gateway's task-adapter assembly (FIX-U3D-14): <c>CHORUS_PLATFORM_ADAPTER</c> names
/// the adapter; the development simulator stays the default mount. Production admission
/// (review 2026-11-13 M2): the adapter's <see cref="IPlatformTaskAdapter.ProductionReady"/>
/// declaration has teeth here — an adapter that declares false mounts only when it is the
/// simulator (whose development-endpoint requirement already makes it a development-only
/// mount) or behind the explicit opt-in variable. The real platform adapters' API contracts
/// are still under joint debugging; filling their credential variables must not walk them
/// into production.
/// </summary>
internal static class GatewayTaskAdapterAssembly
{
    public const string AllowNotProductionReadyVariable = "CHORUS_ALLOW_NOT_PRODUCTION_READY_ADAPTER";

    public static IPlatformTaskAdapter Select(
        string adapterName, Func<string, string> required,
        bool allowNotProductionReady = false)
    {
        IPlatformTaskAdapter adapter = adapterName switch
        {
            "simulator" => new DevelopmentPlatformTaskAdapter(
                new Uri(required("CHORUS_DEV_PLATFORM_ENDPOINT"))),
            "kuaishou" => new KuaishouFulfillmentAdapter(
                new Uri(required("CHORUS_PLATFORM_ENDPOINT")),
                required("CHORUS_PLATFORM_APP_ID"),
                required("CHORUS_PLATFORM_ACCESS_TOKEN"),
                required("CHORUS_PLATFORM_APP_SECRET")),
            "douyin" => new DouyinFulfillmentAdapter(
                new Uri(required("CHORUS_PLATFORM_ENDPOINT")),
                required("CHORUS_PLATFORM_APP_ID")),
            _ => throw new InvalidOperationException(
                $"CHORUS_PLATFORM_ADAPTER names no known platform adapter: {adapterName}"),
        };
        if (!adapter.ProductionReady && !(allowNotProductionReady || adapterName == "simulator"))
        {
            throw new InvalidOperationException(
                $"The '{adapterName}' task adapter is not production ready: its platform API " +
                "contract is still under joint debugging. Set " +
                $"{AllowNotProductionReadyVariable}=true to mount it for development.");
        }
        return adapter;
    }
}
