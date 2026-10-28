using System.Net.Http.Json;

namespace Chorus.Redesign.Platform;

/// <summary>One external operation to carry out; the operation id is the platform's idempotency
/// key — re-issuing the same operation must not double-apply.</summary>
public sealed record PlatformTaskCall(string OperationId, string DesiredJson);

/// <summary>The platform's own record of an operation: whether it executed and what it observed.
/// QueryAsync returns the same shape without issuing anything.</summary>
public sealed record PlatformTaskOutcome(bool Executed, string? ObservedJson);

/// <summary>
/// Executes external platform operations (architecture 6.3). Call carries the desired state and
/// is idempotent by operation id; Query reads the platform's official record, which is the only
/// basis for resolving an outcome the caller could not observe. Adapters for real platforms stay
/// NotProductionReady until their idempotency and query semantics are proven per platform.
/// </summary>
public interface IPlatformTaskAdapter
{
    string Platform { get; }
    bool ProductionReady { get; }
    Task<PlatformTaskOutcome> CallAsync(PlatformTaskCall call, CancellationToken cancellationToken = default);
    Task<PlatformTaskOutcome> QueryAsync(string operationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Development adapter for the simulated platform: a plain HTTP endpoint that records operations
/// idempotently by operation id and answers status queries. It models the one property the task
/// protocol depends on — an official record that survives the death of any executor.
/// </summary>
public sealed class DevelopmentPlatformTaskAdapter(Uri endpoint) : IPlatformTaskAdapter
{
    private static readonly HttpClient Client = new();

    public string Platform => "simulator";
    public bool ProductionReady => false;

    public async Task<PlatformTaskOutcome> CallAsync(PlatformTaskCall call,
        CancellationToken cancellationToken = default)
    {
        var response = await Client.PostAsJsonAsync(new Uri(endpoint, "execute"), new
        {
            operationId = call.OperationId, desired = call.DesiredJson
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlatformTaskOutcome>(cancellationToken)
            ?? throw new InvalidOperationException("The simulated platform returned no outcome.");
    }

    public async Task<PlatformTaskOutcome> QueryAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        var response = await Client.GetAsync(
            new Uri(endpoint, "status?operationId=" + Uri.EscapeDataString(operationId)), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlatformTaskOutcome>(cancellationToken)
            ?? throw new InvalidOperationException("The simulated platform returned no outcome.");
    }
}
