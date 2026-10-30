using System.Globalization;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Platform module assembly on the real gateway host (2026-10-30 pluginization): a
/// deployment that enables a subset of platforms boots the gateway with exactly that
/// subset mounted, and a platform enabled without its credentials refuses to start,
/// naming the missing key in the process output — the operator learns the refusal from
/// the crash itself, not from a silently closed verifier at request time.
/// </summary>
public sealed class RedesignPlatformModuleHostTests
{
    [Fact]
    public async Task TheGatewayStartsWithASubsetOfPlatformsAndStillServesItsFace()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        var port = RedesignM0HostTests.HostEnvironment.FreePort();

        // StartAsync probes the gateway's Ready face to completion: removing three of the
        // four platforms from the configuration leaves a gateway that starts and serves.
        await environment.StartAsync("Chorus.PlatformGateway", port, "platform-gateway",
            GatewayEnvironment(environment.DataPort, new Dictionary<string, string>
            {
                ["Ingress__Platforms__0"] = "bilibili",
                ["Ingress__Bilibili__AccessKeyId"] = "key-id",
                ["Ingress__Bilibili__AccessKeySecret"] = "key-secret",
                ["Ingress__Bilibili__SessionId"] = "session-1",
            }), instanceId: "gateway-subset");

        var logged = await WaitForAsync(
            () => Task.FromResult(environment.AllHostOutput.Contains("platform modules enabled: bilibili")),
            TimeSpan.FromSeconds(15));
        Assert.True(logged, "The gateway never logged which platform modules it mounted.\n" + environment.AllHostOutput);
    }

    [Fact]
    public async Task AnEnabledPlatformWithMissingCredentialsRefusesToStartNamingTheKey()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var port = RedesignM0HostTests.HostEnvironment.FreePort();

        // Ingress:Douyin:AppSecret is deliberately absent: the refusal happens during
        // composition, before the gateway listens, and the process output names the key.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => environment.StartAsync(
            "Chorus.PlatformGateway", port, "platform-gateway",
            GatewayEnvironment(environment.DataPort, new Dictionary<string, string>
            {
                ["Ingress__Platforms__0"] = "douyin",
            }), instanceId: "gateway-missing-key"));

        Assert.Contains("exited with", failure.Message);
        // The stderr capture races the process exit, so the refusal text is awaited.
        var named = await WaitForAsync(
            () => Task.FromResult(environment.AllHostOutput.Contains("Ingress:Douyin:AppSecret")),
            TimeSpan.FromSeconds(15));
        Assert.True(named, "The refusal never named the missing key.\n" + environment.AllHostOutput);
    }

    private static Dictionary<string, string> GatewayEnvironment(int dataPort,
        IReadOnlyDictionary<string, string> platformConfiguration) => new(platformConfiguration)
    {
        ["CHORUS_DATA_PORT"] = dataPort.ToString(CultureInfo.InvariantCulture),
        // No platform tasks are enqueued in these tests; the endpoint is never called.
        ["CHORUS_DEV_PLATFORM_ENDPOINT"] = "http://127.0.0.1:9/",
    };

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(200);
        }
        return await probe();
    }
}
