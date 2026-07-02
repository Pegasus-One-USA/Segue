using System.Reflection;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Reconciles two views of the permission model: the codes declared as constants on
/// <see cref="UnifiedPermissions"/>, and the codes actually enforced in code via
/// <see cref="StandardPermissionAttribute"/>. Startup uses the union to register
/// authorization policies, and the mismatch to flag typos/drift between the two.
/// </summary>
public static class PermissionCatalog
{
    /// <summary>Permission codes declared as public string constants on <see cref="UnifiedPermissions"/>.</summary>
    public static IReadOnlyCollection<string> DeclaredCodes() =>
        typeof(UnifiedPermissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    /// <summary>Permission codes referenced by a <see cref="StandardPermissionAttribute"/> on a controller class or action.</summary>
    public static IReadOnlyCollection<string> DiscoveredCodes(Assembly assembly) =>
        assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(controller => controller
                .GetCustomAttributes<StandardPermissionAttribute>(inherit: true)
                .Concat(controller
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetCustomAttributes<StandardPermissionAttribute>(inherit: true))))
            .Select(a => a.PermissionCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Union of declared and discovered codes — every code that needs a "HasPermission:{code}" policy.</summary>
    public static IReadOnlyCollection<string> AllPermissionCodes(Assembly assembly) =>
        DeclaredCodes()
            .Concat(DiscoveredCodes(assembly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Codes referenced by <see cref="StandardPermissionAttribute"/> that have no matching
    /// <see cref="UnifiedPermissions"/> constant — usually a typo in the attribute argument.
    /// </summary>
    public static IReadOnlyCollection<string> FindUndeclaredCodes(Assembly assembly)
    {
        var declared = new HashSet<string>(DeclaredCodes(), StringComparer.OrdinalIgnoreCase);

        return DiscoveredCodes(assembly)
            .Where(code => !declared.Contains(code))
            .ToArray();
    }
}
