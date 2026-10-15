using System.Text.Json;

namespace FHIRBridge.Application.Security;

/// <summary>One row of the ordered PingOne group to Segue role mapping. A Segue user holds a single role.</summary>
public sealed record PingGroupRoleMapping(string Group, string Role);

/// <summary>
/// Reads, writes and applies the ordered PingOne group to Segue role mapping. The stored form is a JSON array in
/// priority order (<c>[{"group":"id","role":"Admin"}]</c>); earlier shapes (an object of group to role, or group to a
/// role array) are still read, keeping their order and taking the first role.
/// </summary>
public static class PingGroupRoleMapper
{
    public static IReadOnlyList<PingGroupRoleMapping>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = new List<PingGroupRoleMapping>();

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var group = item.TryGetProperty("group", out var g) ? g.GetString() : null;
                    var role = item.TryGetProperty("role", out var r) ? r.GetString() : null;
                    Add(rows, group, role);
                }

                return rows;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var role = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Array when property.Value.GetArrayLength() > 0 => property.Value[0].GetString(),
                        _ => null
                    };
                    Add(rows, property.Name, role);
                }

                return rows;
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A malformed saved value must never open access; callers fall back to the appsettings mappings.
            return null;
        }
    }

    public static string Serialize(IEnumerable<PingGroupRoleMapping> mappings) =>
        JsonSerializer.Serialize(mappings.Select(m => new { group = m.Group, role = m.Role }));

    /// <summary>Converts the appsettings shape (group to role array) into the ordered form.</summary>
    public static IReadOnlyList<PingGroupRoleMapping> FromDictionary(IReadOnlyDictionary<string, string[]> mappings)
    {
        var rows = new List<PingGroupRoleMapping>();
        foreach (var mapping in mappings)
        {
            Add(rows, mapping.Key, mapping.Value?.FirstOrDefault());
        }

        return rows;
    }

    /// <summary>
    /// The distinct Segue role names the user's PingOne groups (ids and/or names) map to, in MAPPING order — so the
    /// first element is the role of the highest-priority row the user matches. Group matching is case-insensitive.
    /// </summary>
    public static IReadOnlyList<string> MapInOrder(IEnumerable<string> groupKeys, IReadOnlyList<PingGroupRoleMapping> mappings)
    {
        var keys = new HashSet<string>(
            groupKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()), StringComparer.OrdinalIgnoreCase);
        var roles = new List<string>();

        foreach (var mapping in mappings)
        {
            if (keys.Contains(mapping.Group.Trim()) &&
                !roles.Contains(mapping.Role, StringComparer.OrdinalIgnoreCase))
            {
                roles.Add(mapping.Role);
            }
        }

        return roles;
    }

    private static void Add(List<PingGroupRoleMapping> rows, string? group, string? role)
    {
        if (!string.IsNullOrWhiteSpace(group) && !string.IsNullOrWhiteSpace(role))
        {
            rows.Add(new PingGroupRoleMapping(group.Trim(), role.Trim()));
        }
    }
}
