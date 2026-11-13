using Chorus.Platform;
using Chorus.PlatformGateway;

namespace Chorus.Engine.Tests;

/// <summary>
/// The gateway's task-adapter assembly admission (review 2026-11-13 M2): the adapter's
/// ProductionReady declaration now refuses the mount at assembly — the simulator stays the
/// development default, and a real platform adapter mounts only behind the explicit opt-in
/// variable. Filling credential variables is not readiness.
/// </summary>
public sealed class RedesignGatewayTaskAdapterAssemblyTests
{
    private static string AnyKey(string key) =>
        key.EndsWith("ENDPOINT", StringComparison.Ordinal) ? "http://127.0.0.1:9/" : "stub-" + key;

    [Fact]
    public void TheSimulatorStaysTheDevelopmentDefaultMount()
    {
        // 模拟器要求开发平台端点才可能被构造，天然是开发挂载：不需要显式准入开关。
        var adapter = GatewayTaskAdapterAssembly.Select("simulator", AnyKey);

        Assert.IsType<DevelopmentPlatformTaskAdapter>(adapter);
        Assert.False(adapter.ProductionReady);
    }

    [Fact]
    public void ANotProductionReadyPlatformAdapterIsRefusedWithoutTheOptIn()
    {
        // ProductionReady=false 至此在装配处有了执行效果：凭据变量齐全也不放行。
        var error = Assert.Throws<InvalidOperationException>(
            () => GatewayTaskAdapterAssembly.Select("kuaishou", AnyKey));

        Assert.Contains("kuaishou", error.Message);
        Assert.Contains("not production ready", error.Message);
        Assert.Contains(GatewayTaskAdapterAssembly.AllowNotProductionReadyVariable, error.Message);
    }

    [Fact]
    public void TheOptInVariableMountsThePlatformAdapterForDevelopment()
    {
        var kuaishou = GatewayTaskAdapterAssembly.Select("kuaishou", AnyKey,
            allowNotProductionReady: true);
        Assert.IsType<KuaishouFulfillmentAdapter>(kuaishou);
        Assert.Equal("kuaishou", kuaishou.Platform);

        var douyin = GatewayTaskAdapterAssembly.Select("douyin", AnyKey,
            allowNotProductionReady: true);
        Assert.IsType<DouyinFulfillmentAdapter>(douyin);
        Assert.Equal("douyin", douyin.Platform);
    }

    [Fact]
    public void AnUnknownAdapterNameStillFailsClosed()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => GatewayTaskAdapterAssembly.Select("discord", AnyKey));

        Assert.Contains("names no known platform adapter", error.Message);
    }
}
