using Chorus.Protocol.Models;

namespace Chorus.Adapters.Abstractions;

public interface IPlatformEventMapper
{
    /// <summary>
    /// The platform this mapper answers (its <see cref="TryMap"/> refuses every other
    /// name). The declaration feeds the module assembly's registration-completeness
    /// check (review 2026-11-13 M6): a mounted ingest module without a mapper declaring
    /// its platform refuses to start, so a forgotten wiring cannot hide behind a
    /// compiling host and answer every real callback with 400 unmapped.
    /// </summary>
    string PlatformName { get; }

    /// <summary>Maps one raw callback body; null means "not mine / not mappable".</summary>
    AudienceEvent? TryMap(string platform, string rawBody, long ingestTimeMs);
}
