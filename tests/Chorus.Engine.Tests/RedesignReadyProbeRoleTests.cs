using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-LINK-01 readiness probes a gateway runs are plane-specific: the streamer gateway
/// answers Ready from the control plane (its routing source) and the platform gateway answers
/// Ready from the data plane (its session source). The probe is the host's own readiness word —
/// a gateway whose critical plane answers nothing must not present itself as Ready. These two
/// facts pin the auth contract of exactly that call: the gateway's role is allowed to answer
/// Ready on the plane it probes. The role was silently missing from both Ready handlers, so a
/// gateway reported Starting forever and the control plane treated it as not-servable.
/// </summary>
public sealed class RedesignReadyProbeRoleTests
{
    [Fact]
    public async Task TheStreamerGatewayRoleAnswersTheControlReadyProbe()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server");
        using var gatewayCertificate = environment.Certificate("@cluster", "streamer-gateway", "gw-probe");
        using var gatewayChannel = environment.Channel(environment.ControlPort, gatewayCertificate, "control-server");
        var ready = await new ControlService.ControlServiceClient(gatewayChannel).ReadyAsync(new Empty());
        Assert.True(ready.Ready);
    }

    [Fact]
    public async Task ThePlatformGatewayRoleAnswersTheDataReadyProbe()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        using var gatewayCertificate = environment.Certificate("@cluster", "platform-gateway", "gw-probe");
        using var gatewayChannel = environment.Channel(environment.DataPort, gatewayCertificate, "data-server");
        var ready = await new DataService.DataServiceClient(gatewayChannel).ReadyAsync(new Empty());
        Assert.True(ready.Ready);
    }
}
