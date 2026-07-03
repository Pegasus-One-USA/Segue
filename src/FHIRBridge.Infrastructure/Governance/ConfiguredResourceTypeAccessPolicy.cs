using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Configuration-driven per-resource-type RBAC. Reads an allow-list of FHIR resource types from
/// <c>Governance:ResourceTypeAccess</c>. When no allow-list is configured, all resource types are
/// permitted (backward-compatible default); when a list is present, only the listed types are allowed.
/// </summary>
public sealed class ConfiguredResourceTypeAccessPolicy : IResourceTypeAccessPolicy
{
    private readonly HashSet<string>? _allowList;

    public ConfiguredResourceTypeAccessPolicy(IConfiguration configuration)
    {
        _allowList = LoadAllowList(configuration);
    }

    public bool IsResourceTypeAllowed(string resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceType))
        {
            return false;
        }

        // No allow-list configured => allow everything (opt-in restriction).
        if (_allowList is null)
        {
            return true;
        }

        return _allowList.Contains(resourceType.Trim());
    }

    private static HashSet<string>? LoadAllowList(IConfiguration configuration)
    {
        var types = configuration.GetSection("Governance:ResourceTypeAccess").Get<string[]>();

        return types is { Length: > 0 }
            ? new HashSet<string>(types, StringComparer.OrdinalIgnoreCase)
            : null;
    }
}
