using System.Text.Json;

namespace Chorus.StreamerGateway;

/// <summary>
/// The (platform, appId) → (tenant, game) map the public edge uses to name a tenant. The
/// gateway process itself is cluster-scoped — one certificate serving every tenant's clients
/// — so the tenant of a login can only come from the app the client presents, and the mapping
/// must be unambiguous: an appId claimed by two tenants fails the host at startup rather than
/// routing one tenant's players into another tenant's data (fail-closed).
/// </summary>
public sealed class TenantApplicationMap
{
    private readonly Dictionary<string, (string TenantId, string GameId)> _byAppId
        = new(StringComparer.Ordinal);

    public TenantApplicationMap(string tenantApplicationsJson)
    {
        using var document = JsonDocument.Parse(tenantApplicationsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("CHORUS_TENANT_APPLICATIONS must be a JSON object.");
        foreach (var tenant in document.RootElement.EnumerateObject())
        {
            if (tenant.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Tenant {tenant.Name} must map app ids to game ids.");
            foreach (var application in tenant.Value.EnumerateObject())
            {
                if (application.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException($"App {application.Name} must map to a game id string.");
                if (!_byAppId.TryAdd(application.Name, (tenant.Name, application.Value.GetString()!)))
                    throw new InvalidOperationException(
                        $"App id {application.Name} is claimed by tenants {_byAppId[application.Name].TenantId} and {tenant.Name}; the edge cannot tell them apart.");
            }
        }
        if (_byAppId.Count == 0)
            throw new InvalidOperationException("CHORUS_TENANT_APPLICATIONS is empty.");
    }

    public bool TryResolve(string appId, out string tenantId, out string gameId)
    {
        if (_byAppId.TryGetValue(appId, out var resolved))
        {
            tenantId = resolved.TenantId;
            gameId = resolved.GameId;
            return true;
        }
        tenantId = "";
        gameId = "";
        return false;
    }
}
