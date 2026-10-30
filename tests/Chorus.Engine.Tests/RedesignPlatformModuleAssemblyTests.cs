using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Engine.Routing;
using Chorus.Protocol.Models;
using Chorus.Platform;
using Chorus.PlatformGateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// Platform module assembly (2026-10-30 pluginization): the gateway mounts only the
/// platforms Ingress:Platforms lists. These are the composition invariants — an unlisted
/// platform contributes zero registrations and zero credential reads, an enabled platform
/// with missing credentials refuses to start naming the keys (before anything registers),
/// and the capability registry is generated from the enabled list, not the compiled fleet.
/// </summary>
public sealed class RedesignPlatformModuleAssemblyTests
{
    [Fact]
    public void AnEmptyPlatformListMountsNoPlatformServices()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();

        var enabled = PlatformModuleAssembly.AddPlatformModules(services, configuration);

        Assert.Empty(enabled);
        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IPlatformEventMapper>());
        Assert.Empty(provider.GetServices<IPlatformNamedEventVerifier>());
        Assert.Equal(0, provider.GetRequiredService<PlatformCapabilityRegistry>().Count);
    }

    [Fact]
    public void OnlyTheListedPlatformMountsAndTheUnlistedAreNeverRead()
    {
        var services = new ServiceCollection();
        var configuration = new CountingConfiguration(Configuration(new Dictionary<string, string?>
        {
            ["Ingress:Platforms:0"] = "douyin",
            ["Ingress:Douyin:AppSecret"] = "secret-a",
        }));

        var enabled = PlatformModuleAssembly.AddPlatformModules(services, configuration);

        Assert.Equal(["douyin"], enabled.Select(module => module.Name));
        using var provider = services.BuildServiceProvider();
        // The enabled platform's verifier, router, and mapper resolve.
        Assert.IsType<PlatformEventVerifierRouter>(provider.GetRequiredService<IPlatformEventVerifier>());
        Assert.NotNull(provider.GetRequiredService<DouyinPlatformEventVerifier>());
        Assert.Contains(provider.GetServices<IPlatformEventMapper>(), mapper => mapper is DouyinPlatformEventMapper);
        // The unlisted platforms registered nothing the container can resolve.
        Assert.DoesNotContain(provider.GetServices<IPlatformEventMapper>(), mapper => mapper is KuaishouPlatformEventMapper or BilibiliPlatformEventMapper);
        Assert.Null(provider.GetService<KuaishouPlatformEventVerifier>());
        Assert.Null(provider.GetService<BilibiliPlatformEventVerifier>());
        // The capability registry is generated from the list: douyin answers, bilibili does not.
        var registry = provider.GetRequiredService<PlatformCapabilityRegistry>();
        Assert.Equal(1, registry.Count);
        Assert.True(registry.SupportsEventKind("douyin", AudienceEventKind.Comment));
        Assert.False(registry.SupportsEventKind("bilibili", AudienceEventKind.Comment));
        // Zero credential reads for every unlisted platform — not deferred, never issued.
        Assert.DoesNotContain(configuration.Reads, key => key.Contains("Kuaishou", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(configuration.Reads, key => key.Contains("Bilibili", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(configuration.Reads, key => key.Contains("MiniGame", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnEnabledPlatformWithMissingCredentialsRefusesToStartNamingTheKeys()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Ingress:Platforms:0"] = "kuaishou",
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => PlatformModuleAssembly.AddPlatformModules(services, configuration));

        Assert.Contains("Ingress:Kuaishou:VerifyToken", error.Message);
        Assert.Contains("Ingress:Kuaishou:MsgEncryptKey", error.Message);
        // Validation runs before any registration: the refused start leaves nothing mounted.
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IPlatformEventMapper)
            || descriptor.ServiceType == typeof(IPlatformNamedEventVerifier)
            || descriptor.ServiceType == typeof(IPlatformEventVerifier)
            || descriptor.ServiceType == typeof(PlatformCapabilityRegistry));
    }

    [Fact]
    public void AnUnknownPlatformNameFailsClosedListingTheModules()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Ingress:Platforms:0"] = "discord",
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => PlatformModuleAssembly.AddPlatformModules(services, configuration));

        Assert.Contains("Unknown platform 'discord'", error.Message);
        Assert.Contains("bilibili", error.Message);
        Assert.Contains("douyin", error.Message);
        Assert.Contains("kuaishou", error.Message);
        Assert.Contains("minigame", error.Message);
    }

    [Fact]
    public void APlatformListedTwiceIsAConfigurationError()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Ingress:Platforms:0"] = "douyin",
            ["Ingress:Platforms:1"] = "douyin",
            ["Ingress:Douyin:AppSecret"] = "secret-a",
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => PlatformModuleAssembly.SelectEnabledModules(configuration));

        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void TheMiniGameModuleMountsWithoutAnEventCapabilityRow()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Ingress:Platforms:0"] = "bilibili",
            ["Ingress:Platforms:1"] = "minigame",
            ["Ingress:Bilibili:AccessKeyId"] = "key-id",
            ["Ingress:Bilibili:AccessKeySecret"] = "key-secret",
            ["Ingress:Bilibili:SessionId"] = "session-1",
            ["Ingress:MiniGame:Douyin:AppId"] = "mg-app",
            ["Ingress:MiniGame:Douyin:AppSecret"] = "mg-secret",
        });

        var enabled = PlatformModuleAssembly.AddPlatformModules(services, configuration);

        Assert.Equal(["bilibili", "minigame"], enabled.Select(module => module.Name));
        using var provider = services.BuildServiceProvider();
        // The mini-game login surface resolves; the event registry carries only the event platform.
        Assert.NotNull(provider.GetRequiredService<DouyinMiniGameAuthClient>());
        var registry = provider.GetRequiredService<PlatformCapabilityRegistry>();
        Assert.Equal(1, registry.Count);
        Assert.True(registry.SupportsEventKind("bilibili", AudienceEventKind.Enter));
        Assert.False(registry.SupportsEventKind("minigame", AudienceEventKind.Comment));
        // The bridge stays down without its full credential set.
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void TheBilibiliBridgeMountsOnlyOnTheFullCredentialSet()
    {
        var bridgeKeys = new Dictionary<string, string?>
        {
            ["Ingress:Bilibili:AccessKeyId"] = "key-id",
            ["Ingress:Bilibili:AccessKeySecret"] = "key-secret",
            ["Ingress:Bilibili:SessionId"] = "session-1",
            ["Ingress:Bilibili:AppId"] = "123456",
            ["Ingress:Bilibili:RoomOwnerAuthCode"] = "auth-code",
            ["Ingress:Bilibili:IngressUrl"] = "https://ingress.example/chorus",
        };

        var withBridge = new ServiceCollection();
        withBridge.AddLogging();
        PlatformModuleAssembly.AddPlatformModules(withBridge, Configuration(
            BridgeConfiguration(bridgeKeys, withBridge: true)));
        Assert.Contains(withBridge, descriptor => descriptor.ServiceType == typeof(IHostedService));

        var withoutBridge = new ServiceCollection();
        PlatformModuleAssembly.AddPlatformModules(withoutBridge, Configuration(
            BridgeConfiguration(bridgeKeys, withBridge: false)));
        Assert.DoesNotContain(withoutBridge, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AllowAllMountsTheDevelopmentVerifierAndTestMapper()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Ingress:Platforms:0"] = "douyin",
            ["Ingress:Douyin:AppSecret"] = "secret-a",
            ["Ingress:AllowAll"] = "true",
        });

        PlatformModuleAssembly.AddPlatformModules(services, configuration);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<AllowAllPlatformEventVerifier>());
        Assert.Contains(provider.GetServices<IPlatformEventMapper>(), mapper => mapper is TestPlatformEventMapper);
        // The simulator mapper rides along: it is the one platform a development fleet can
        // mount, so ingress-face parity runs on it.
        Assert.Contains(provider.GetServices<IPlatformEventMapper>(), mapper => mapper is SimulatorPlatformEventMapper);
    }

    [Fact]
    public void TheMiniGameModuleNamesItsMissingCredentials()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Ingress:Platforms:0"] = "minigame",
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => PlatformModuleAssembly.AddPlatformModules(new ServiceCollection(), configuration));

        Assert.Contains("Ingress:MiniGame:Douyin:AppId", error.Message);
        Assert.Contains("Ingress:MiniGame:Douyin:AppSecret", error.Message);
    }

    private static Dictionary<string, string?> BridgeConfiguration(
        Dictionary<string, string?> bridgeKeys, bool withBridge)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["Ingress:Platforms:0"] = "bilibili",
        };
        foreach (var (key, value) in bridgeKeys)
        {
            if (withBridge || key is not ("Ingress:Bilibili:AppId" or "Ingress:Bilibili:RoomOwnerAuthCode" or "Ingress:Bilibili:IngressUrl"))
            {
                pairs[key] = value;
            }
        }
        return pairs;
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? pairs = null)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs ?? []).Build();

    /// <summary>
    /// Records every direct key read so a test can assert that an unlisted platform's
    /// credentials are never touched — the pluginization's zero-read guarantee, observed
    /// instead of trusted.
    /// </summary>
    private sealed class CountingConfiguration(IConfiguration inner) : IConfiguration
    {
        private readonly List<string> _reads = [];

        public IReadOnlyList<string> Reads => _reads;

        public string? this[string key]
        {
            get
            {
                _reads.Add(key);
                return inner[key];
            }
            set => inner[key] = value;
        }

        public IConfigurationSection GetSection(string key) => inner.GetSection(key);

        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();

        public IChangeToken GetReloadToken() => inner.GetReloadToken();
    }
}
