using System.Reflection;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Reconciles two views of the permission model: the codes declared as built-in seed data in
/// <see cref="RbacSeedData"/>, and the codes actually enforced in code — via a fixed
/// <see cref="StandardPermissionAttribute"/>, or via a <see cref="DynamicSourceSystemPermissionAttribute"/>
/// crossed with every value of its declared enum type (resolved through
/// <see cref="SourceSystemPermissionGroups.AllGroupsFor"/>) for endpoints whose required group is only
/// known once the request is inspected. Startup uses the union to register authorization policies, and
/// the mismatch to flag typos/drift between the two.
/// </summary>
public static class PermissionCatalog
{
    /// <summary>
    /// A permission discovered via <see cref="StandardPermissionAttribute"/>, with its full taxonomy.
    /// <paramref name="Description"/> is the combined text when the same permission is declared on more than
    /// one class/method with different descriptions. <paramref name="Instances"/> is every
    /// "ClassName.MethodName" (or just "ClassName" for a class-level attribute) where it's declared,
    /// comma-separated. <paramref name="IsDynamic"/> is <see langword="true"/> when this entry came from
    /// crossing a <see cref="DynamicSourceSystemPermissionAttribute"/> rather than a real
    /// <see cref="StandardPermissionAttribute"/> — see <see cref="FindUndeclaredCodes"/>.
    /// </summary>
    public sealed record DiscoveredPermission(
        PermissionGroupCode Group,
        PermissionActionCode Action,
        string Code,
        string? Description,
        string Instances,
        bool IsDynamic = false)
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
    /// Every <see cref="StandardPermissionAttribute"/> on a controller class or action, plus every
    /// permission implied by a <see cref="DynamicSourceSystemPermissionAttribute"/> action crossed with
    /// every value of its declared enum type — full taxonomy either way.
    /// The same permission code can be declared/implied more than once (e.g. two actions that both
    /// require "user.view", or two source-connection endpoints that both dynamically check an Edit
    /// permission) — those occurrences are combined into a single entry rather than the earlier ones
    /// being silently dropped: distinct descriptions are joined with " | ", and every declaring
    /// class/method is recorded in <see cref="DiscoveredPermission.Instances"/>.
    /// </summary>
    public static IReadOnlyCollection<DiscoveredPermission> DiscoveredPermissions(Assembly assembly)
    {
        var occurrences = new List<(StandardPermissionAttribute Attribute, string Location)>();
        var dynamicOccurrences = new List<(DynamicSourceSystemPermissionAttribute Attribute, string Location)>();

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

                foreach (var attribute in method.GetCustomAttributes<DynamicSourceSystemPermissionAttribute>(inherit: true))
                {
                    dynamicOccurrences.Add((attribute, $"{controller.Name}.{method.Name}"));
                }
            }
        }

        var discoveredPermissions = new List<DiscoveredPermission>();

        foreach (var occurrencesForCode in occurrences.GroupBy(o => o.Attribute.PermissionCode, StringComparer.OrdinalIgnoreCase))
        {
            var firstOccurrence = occurrencesForCode.First().Attribute;

            var descriptions = occurrencesForCode
                .Select(o => o.Attribute.Description)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var combinedDescription = descriptions.Length > 0 ? string.Join(" | ", descriptions) : null;

            var instances = string.Join(
                ", ",
                occurrencesForCode.Select(o => o.Location).Distinct(StringComparer.OrdinalIgnoreCase));

            discoveredPermissions.Add(new DiscoveredPermission(
                firstOccurrence.Group,
                firstOccurrence.Action,
                firstOccurrence.PermissionCode,
                combinedDescription,
                instances));
        }

        // Cross each declared (enum type, action) pair with every group a value of that enum can
        // resolve to — a new enum value, or a new same-named PermissionGroupCode member for an
        // existing one, gets its permission the next boot, no attribute or seed-data edit required at
        // that call site.
        foreach (var occurrencesForAction in dynamicOccurrences.GroupBy(o => (o.Attribute.EnumType, o.Attribute.Action)))
        {
            var descriptions = occurrencesForAction
                .Select(o => o.Attribute.Description)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var combinedDescription = descriptions.Length > 0 ? string.Join(" | ", descriptions) : null;

            var instances = string.Join(
                ", ",
                occurrencesForAction.Select(o => o.Location).Distinct(StringComparer.OrdinalIgnoreCase));

            var (enumType, action) = occurrencesForAction.Key;

            foreach (var group in SourceSystemPermissionGroups.AllGroupsFor(enumType))
            {
                var code = PermissionTaxonomy.BuildPermissionCode(group, action);

                // Already covered by a real [StandardPermission] usage — don't add a duplicate.
                if (discoveredPermissions.Any(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // The attribute's description is written once but crossed with every group in the enum,
                // so it reads identically for Epic/Cerner/Allscripts/... without the group name appended.
                var groupDescription = combinedDescription is null
                    ? null
                    : $"{combinedDescription} ({group.GetDisplayName()})";

                discoveredPermissions.Add(new DiscoveredPermission(group, action, code, groupDescription, instances, IsDynamic: true));
            }
        }

        return discoveredPermissions;
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
    /// Excludes codes discovered via <see cref="DynamicSourceSystemPermissionAttribute"/>: those are
    /// expected to have no seed entry (that's the point of auto-discovery), so including them here
    /// would flag every one of them, every boot, as a false-positive "typo".
    /// </summary>
    public static IReadOnlyCollection<string> FindUndeclaredCodes(Assembly assembly)
    {
        var declared = new HashSet<string>(DeclaredCodes(), StringComparer.OrdinalIgnoreCase);

        return DiscoveredPermissions(assembly)
            .Where(p => !p.IsDynamic && !declared.Contains(p.Code))
            .Select(p => p.Code)
            .ToArray();
    }
}
