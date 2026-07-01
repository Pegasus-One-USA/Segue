using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Configuration-driven per-resource-type RBAC. Reads an allow-list of FHIR resource types per tenant from
/// <c>Governance:ResourceTypeAccess:{tenantId}</c>. When a tenant has no configured allow-list, all resource types are
/// permitted (backward-compatible default); when a list is present, only the listed types are allowed.
/// </summary>
public sealed class ConfiguredResourceTypeAccessPolicy : IResourceTypeAccessPolicy
{
    private readonly IReadOnlyDictionary<Guid, HashSet<string>> _allowListsByTenant;

    public ConfiguredResourceTypeAccessPolicy(IConfiguration configuration)
    {
        _allowListsByTenant = LoadAllowLists(configuration);
    }

    public bool IsResourceTypeAllowed(Guid tenantId, string resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceType))
        {
            return false;
        }

        // No allow-list configured for the tenant => allow everything (opt-in restriction).
        if (!_allowListsByTenant.TryGetValue(tenantId, out var allowed))
        {
            return true;
        }

        return allowed.Contains(resourceType.Trim());
    }

    private static IReadOnlyDictionary<Guid, HashSet<string>> LoadAllowLists(IConfiguration configuration)
    {
        var result = new Dictionary<Guid, HashSet<string>>();
        var section = configuration.GetSection("Governance:ResourceTypeAccess");

        foreach (var tenantSection in section.GetChildren())
        {
            if (!Guid.TryParse(tenantSection.Key, out var tenantId))
            {
                continue;
            }

            var types = tenantSection.Get<string[]>();
            if (types is { Length: > 0 })
            {
                result[tenantId] = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
            }
        }

        return result;
    }
}
