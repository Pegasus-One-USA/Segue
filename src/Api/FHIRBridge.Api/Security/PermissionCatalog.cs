using System.Reflection;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Reconciles two views of the permission model: the codes declared as built-in seed data in
/// <see cref="RbacSeedData"/>, and the codes actually enforced in code via
/// <see cref="StandardPermissionAttribute"/>. Startup uses the union to register
/// authorization policies, and the mismatch to flag typos/drift between the two.
/// </summary>
public static class PermissionCatalog
{
    /// <summary>
    /// A permission discovered via <see cref="StandardPermissionAttribute"/>, with its full taxonomy.
    /// <paramref name="Description"/> is the combined text when the same permission is declared on more than
    /// one class/method with different descriptions. <paramref name="Instances"/> is every
    /// "ClassName.MethodName" (or just "ClassName" for a class-level attribute) where it's declared,
    /// comma-separated.
    /// </summary>
    public sealed record DiscoveredPermission(
        PermissionGroupCode Group,
        PermissionActionCode Action,
        string Code,
        string? Description,
        string Instances)
    {
        public Guid Id
        {
            get { return PermissionTaxonomy.BuildPermissionId(Group, Action); }
        }
    }

    /// <summary>Permission codes declared as built-in seed data on <see cref="RbacSeedData.Permissions"/>.</summary>
    public static IReadOnlyCollection<string> DeclaredCodes()
    {
        return RbacSeedData.Permissions
            .Select(p => p.Name)
            .ToArray();
    }

    /// <summary>
    /// Every <see cref="StandardPermissionAttribute"/> on a controller class or action, with its full taxonomy.
    /// The same permission code can be declared on more than one class/method (e.g. two actions that both
    /// require "user.view") — those occurrences are combined into a single entry rather than the earlier ones
    /// being silently dropped: distinct descriptions are joined with " | ", and every declaring
    /// class/method is recorded in <see cref="DiscoveredPermission.Instances"/>.
    /// </summary>
    public static IReadOnlyCollection<DiscoveredPermission> DiscoveredPermissions(Assembly assembly)
    {
        var occurrences = new List<(StandardPermissionAttribute Attribute, string Location)>();

        foreach (var controller in assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t)))
        {
            foreach (var attribute in controller.GetCustomAttributes<StandardPermissionAttribute>(inherit: true))
            {
                occurrences.Add((attribute, controller.Name));
            }

            var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                foreach (var attribute in method.GetCustomAttributes<StandardPermissionAttribute>(inherit: true))
                {
                    occurrences.Add((attribute, $"{controller.Name}.{method.Name}"));
                }
            }
        }

        var permissions = new List<DiscoveredPermission>();

        foreach (var group in occurrences.GroupBy(o => o.Attribute.PermissionCode, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First().Attribute;

            var descriptions = group
                .Select(o => o.Attribute.Description)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var combinedDescription = descriptions.Length > 0 ? string.Join(" | ", descriptions) : null;

            var instances = string.Join(
                ", ",
                group.Select(o => o.Location).Distinct(StringComparer.OrdinalIgnoreCase));

            permissions.Add(new DiscoveredPermission(first.Group, first.Action, first.PermissionCode, combinedDescription, instances));
        }

        return permissions;
    }

    /// <summary>Permission codes referenced by a <see cref="StandardPermissionAttribute"/> on a controller class or action.</summary>
    public static IReadOnlyCollection<string> DiscoveredCodes(Assembly assembly)
    {
        return DiscoveredPermissions(assembly)
            .Select(p => p.Code)
            .ToArray();
    }

    /// <summary>Union of declared and discovered codes — every code that needs a "HasPermission:{code}" policy.</summary>
    public static IReadOnlyCollection<string> AllPermissionCodes(Assembly assembly)
    {
        return DeclaredCodes()
            .Concat(DiscoveredCodes(assembly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Codes referenced by <see cref="StandardPermissionAttribute"/> that have no matching
    /// <see cref="RbacSeedData.Permissions"/> entry — usually a typo in the attribute argument.
    /// </summary>
    public static IReadOnlyCollection<string> FindUndeclaredCodes(Assembly assembly)
    {
        var declared = new HashSet<string>(DeclaredCodes(), StringComparer.OrdinalIgnoreCase);

        return DiscoveredCodes(assembly)
            .Where(code => !declared.Contains(code))
            .ToArray();
    }
}
