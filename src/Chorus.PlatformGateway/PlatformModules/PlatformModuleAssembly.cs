using Chorus.Adapters.Abstractions;
using Chorus.Adapters.Platforms;
using Chorus.Engine.Routing;
using Chorus.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.PlatformGateway;

/// <summary>
/// Configuration-driven platform assembly (platform pluginization, 2026-10-30): the
/// gateway mounts only the modules <c>Ingress:Platforms</c> lists. An unlisted platform
/// contributes zero service registrations and zero credential reads; an enabled module
/// with missing credentials refuses to start naming the keys; an unknown name in the
/// list is a configuration error, not a silent no-op. All enabled modules validate
/// before any registers, so a refused startup leaves no partial wiring behind. The
/// same loop runs production admission (2026-11-13 review M2): a module whose protocol
/// declares <c>ProductionReady: false</c> is refused from a production host — complete
/// credential keys are configuration, not protocol verification. The loop also checks
/// registration completeness (review 2026-11-13 M6): an ingest module that forgot its
/// mapper or named verifier refuses to start — a compiling host must not hide a missing wire.
/// </summary>
public static class PlatformModuleAssembly
{
    public const string PlatformsConfigurationKey = "Ingress:Platforms";
    public const string AllowAllConfigurationKey = "Ingress:AllowAll";

    /// <summary>The modules this gateway carries, in stable list order.</summary>
    public static IReadOnlyList<IPlatformModule> AvailableModules() =>
        [new BilibiliPlatformModule(), new DouyinPlatformModule(), new KuaishouPlatformModule(), new MiniGamePlatformModule()];

    /// <summary>
    /// Resolves the enabled modules from <c>Ingress:Platforms</c>. Blank entries are
    /// ignored; a duplicated or unknown name throws — fail closed, so a typo cannot
    /// disable a platform the operator thought they enabled.
    /// </summary>
    public static IReadOnlyList<IPlatformModule> SelectEnabledModules(
        IConfiguration configuration, IReadOnlyCollection<IPlatformModule>? available = null)
    {
        available ??= AvailableModules();
        var listed = (configuration.GetSection(PlatformsConfigurationKey).Get<string[]>() ?? [])
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .ToList();

        var enabled = new List<IPlatformModule>(listed.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = string.Join(", ", available.Select(module => $"'{module.Name}'"));
        foreach (var name in listed)
        {
            var module = available.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                throw new InvalidOperationException(
                    $"Unknown platform '{name}' in {PlatformsConfigurationKey}. Available modules: {known}.");
            }
            if (!seen.Add(name))
            {
                throw new InvalidOperationException(
                    $"Platform '{name}' appears more than once in {PlatformsConfigurationKey}.");
            }
            enabled.Add(module);
        }
        return enabled;
    }

    /// <summary>
    /// Validates and mounts the enabled platform modules onto <paramref name="services"/>:
    /// per-module registrations, the shared verifier router over the enabled named
    /// verifiers, and the capability registry seeded from the enabled list. Returns the
    /// enabled modules so the host can log what it mounted.
    /// </summary>
    /// <param name="allowNotProductionReadyModules">
    /// The development opt-in (production admission, review 2026-11-13 M2): when false —
    /// every production host — a module declaring <c>ProductionReady: false</c> refuses to
    /// mount even with its credential keys complete; a module whose protocol is still a
    /// placeholder does not reach production merely by existing in configuration. The
    /// host passes <c>Ingress:AllowNotProductionReady</c> through, so only a deployment
    /// that names the switch mounts an unverified protocol.
    /// </param>
    /// <param name="available">
    /// The module fleet to select from; defaults to the gateway's own. Test seam, mirroring
    /// <see cref="SelectEnabledModules"/> — the completeness check must be assertable
    /// against a module whose wiring is deliberately incomplete.
    /// </param>
    public static IReadOnlyList<IPlatformModule> AddPlatformModules(
        IServiceCollection services, IConfiguration configuration,
        bool allowNotProductionReadyModules = false,
        IReadOnlyCollection<IPlatformModule>? available = null)
    {
        var enabled = SelectEnabledModules(configuration, available);

        // Validate every enabled module before any registers: startup is atomic — the
        // first refusing module stops the whole start instead of leaving a half-mounted
        // fleet behind.
        foreach (var module in enabled)
        {
            // Production admission runs ahead of credential validation (review 2026-11-13
            // M2): a placeholder protocol is refused no matter how complete its keys are —
            // "fix the missing key" must not be the advice that walks a placeholder into
            // production. Development rides past via the explicit opt-in.
            if (!module.Capabilities.ProductionReady && !allowNotProductionReadyModules)
            {
                throw new InvalidOperationException(
                    $"Platform '{module.Name}' is not production ready: its wire protocol is " +
                    "still under joint debugging and must not serve production traffic. " +
                    "Set 'Ingress:AllowNotProductionReady' to true to mount it for development.");
            }
            module.ValidateConfiguration(configuration);
        }

        foreach (var module in enabled)
        {
            module.ConfigureServices(services, configuration);
        }

        var ingestModules = enabled
            .Where(module => (module.Capabilities.Surfaces & PlatformModuleSurface.EventIngest) != 0)
            .ToList();
        if (ingestModules.Count > 0)
        {
            // Ingress:AllowAll (default false) keeps the unsigned dev verifier mounted; without
            // it the router fails closed for platforms without a real verifier. The simulator
            // mapper rides along with the test mapper: the simulator is the one platform a
            // development fleet can mount, so it is the one whose events can be driven through
            // the ingress face into a room.
            if (configuration.GetValue(AllowAllConfigurationKey, false))
            {
                services.AddSingleton<AllowAllPlatformEventVerifier>();
                services.AddSingleton<IPlatformEventMapper, TestPlatformEventMapper>();
                services.AddSingleton<IPlatformEventMapper, SimulatorPlatformEventMapper>();
            }
            services.AddSingleton(sp => new PlatformEventVerifierRouter(
                sp.GetServices<IPlatformNamedEventVerifier>(),
                fallback: sp.GetService<AllowAllPlatformEventVerifier>()));
            services.AddSingleton<IPlatformEventVerifier>(sp => sp.GetRequiredService<PlatformEventVerifierRouter>());

            // Registration completeness (review 2026-11-13 M6): compiling is not wiring.
            // Every mounted ingest module must have contributed a mapper AND a named
            // verifier declaring its platform — a module that forgot either would compile
            // and start, then answer every real callback with 400 unmapped (or slide it
            // to the development fallback). A throwaway provider resolves the
            // registrations exactly as the host will see them; throwing here keeps the
            // startup atomic (nothing mounts because the host never finishes starting).
            using var probe = services.BuildServiceProvider();
            var mapperPlatforms = probe.GetServices<IPlatformEventMapper>()
                .Select(mapper => mapper.PlatformName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var verifierPlatforms = probe.GetServices<IPlatformNamedEventVerifier>()
                .Select(verifier => verifier.PlatformName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var module in ingestModules)
            {
                if (!mapperPlatforms.Contains(module.Name))
                {
                    throw new InvalidOperationException(
                        $"Platform '{module.Name}' is enabled in {PlatformsConfigurationKey} but no " +
                        "registered event mapper declares it: the module's ingest wiring is incomplete. " +
                        "Every ingest module must register an IPlatformEventMapper whose PlatformName " +
                        "matches the module name.");
                }

                if (!verifierPlatforms.Contains(module.Name))
                {
                    throw new InvalidOperationException(
                        $"Platform '{module.Name}' is enabled in {PlatformsConfigurationKey} but no " +
                        "registered verifier declares it: the module's ingest wiring is incomplete. " +
                        "Every ingest module must register an IPlatformNamedEventVerifier whose " +
                        "PlatformName matches the module name.");
                }
            }
        }

        // The capability registry is generated from the enabled list, not from the compiled-in
        // fleet: SupportsEventKind for an unlisted platform answers false.
        services.AddSingleton(_ =>
        {
            var registry = new PlatformCapabilityRegistry();
            foreach (var module in ingestModules)
            {
                registry.Register(module.Name,
                    new PlatformCapabilities(module.Capabilities.EventKinds.ToHashSet()));
            }
            return registry;
        });
        return enabled;
    }
}

/// <summary>
/// Fail-fast credential check shared by the modules: an enabled module whose keys are
/// blank refuses to start naming every missing key, with the way out (set them, or
/// remove the platform from the list).
/// </summary>
internal static class PlatformModuleConfiguration
{
    public static void RequireKeys(IConfiguration configuration, string module, params string[] keys)
    {
        var missing = keys.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Platform '{module}' is enabled in {PlatformModuleAssembly.PlatformsConfigurationKey} " +
                $"but its configuration is incomplete: {string.Join(", ", missing.Select(key => $"'{key}'"))}. " +
                $"Set the missing keys or remove '{module}' from the platform list.");
        }
    }
}
