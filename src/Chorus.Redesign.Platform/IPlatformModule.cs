using Chorus.Protocol.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.Redesign.Platform;

/// <summary>Which gateway surfaces one platform module mounts.</summary>
[Flags]
public enum PlatformModuleSurface
{
    None = 0,

    /// <summary>Platform events can be ingested (verifier + event mapper).</summary>
    EventIngest = 1,

    /// <summary>Login credential verification (e.g. a mini-game code2session client).</summary>
    Login = 2,

    /// <summary>Chorus can write results back to the platform (e.g. rank upload).</summary>
    Writeback = 4,
}

/// <summary>
/// Declared capabilities of one platform module: what the module mounts, and — for
/// <see cref="PlatformModuleSurface.EventIngest"/> modules — which audience event kinds
/// its mapper actually produces. A module with no event surface declares an empty set.
/// </summary>
/// <param name="Surfaces">Surfaces the module mounts on the gateway.</param>
/// <param name="EventKinds">Event kinds the mapper produces; empty for non-ingest modules.</param>
public sealed record PlatformModuleCapabilities(
    PlatformModuleSurface Surfaces,
    IReadOnlySet<AudienceEventKind> EventKinds)
{
    public static PlatformModuleCapabilities EventIngest(params AudienceEventKind[] kinds)
        => new(PlatformModuleSurface.EventIngest, kinds.ToHashSet());
}

/// <summary>
/// One platform integration the gateway can enable (platform pluginization, 2026-10-30):
/// different deployments face different platforms, so the gateway composes from a module
/// list instead of compiling every adapter in unconditionally. A module is enabled by
/// naming it in the gateway's platform list; an unlisted module contributes zero service
/// registrations and zero credential reads, and an enabled module whose credentials are
/// missing must refuse to start, naming the missing key (fail fast, fail closed).
///
/// Implementations live host-side (they wire concrete adapters); this contract stays
/// free of any concrete platform so the abstraction does not drag adapters into every
/// consumer.
/// </summary>
public interface IPlatformModule
{
    /// <summary>Module name as it appears in the gateway's platform list (e.g. "bilibili").</summary>
    string Name { get; }

    /// <summary>What this module mounts; the source for the capability registry's rows.</summary>
    PlatformModuleCapabilities Capabilities { get; }

    /// <summary>
    /// Fail-fast configuration check, called only when the module is enabled and before
    /// any module registers anything: an enabled module with missing credentials throws
    /// <see cref="InvalidOperationException"/> naming every missing configuration key.
    /// </summary>
    void ValidateConfiguration(IConfiguration configuration);

    /// <summary>
    /// Register the module's services. Called only when the module is enabled and its
    /// configuration has passed <see cref="ValidateConfiguration"/>; reads credentials
    /// from the module's own configuration section only.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
