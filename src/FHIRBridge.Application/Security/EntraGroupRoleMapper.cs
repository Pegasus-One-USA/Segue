namespace FHIRBridge.Application.Security;

/// <summary>
/// Projects the Entra <c>groups</c> claim values of a signed-in user onto the five built-in
/// FHIRBridge roles using the configured <see cref="EntraAuthenticationOptions.GroupRoleMappings"/>.
/// Pure and side-effect free so it can be unit tested independently of the ASP.NET pipeline.
/// </summary>
public static class EntraGroupRoleMapper
{
    /// <summary>
    /// Returns the distinct set of FHIRBridge role names that the supplied Entra group claim values
    /// grant, according to <paramref name="mappings"/>. Group keys and role names are matched
    /// case-insensitively; unknown groups and blank entries are ignored.
    /// </summary>
    public static IReadOnlyCollection<string> MapGroupsToRoles(
        IEnumerable<string>? groupClaimValues,
        IReadOnlyDictionary<string, string[]>? mappings)
    {
        if (groupClaimValues is null || mappings is null || mappings.Count == 0)
        {
            return [];
        }

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groupClaimValues)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                continue;
            }

            foreach (var mapping in mappings)
            {
                if (!string.Equals(mapping.Key, group.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var role in mapping.Value ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(role))
                    {
                        roles.Add(role.Trim());
                    }
                }
            }
        }

        return roles;
    }
}
