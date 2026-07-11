using System.Reflection;

namespace FHIRBridge.Application.Security;

/// <summary>
/// Validates that the standardized permission taxonomy has no duplicate values before
/// <see cref="RbacSeedData"/> is used to seed/sync the database. A duplicate underlying enum value, a
/// duplicate declared Id, a duplicate display name, or two <see cref="RbacSeedData.Permissions"/> entries
/// for the same (Group, Action) pair would each silently corrupt the deterministic Id/Code scheme
/// <see cref="PermissionTaxonomy"/> is built on (two concepts sharing one database row, or one concept
/// splitting across two). Run once at startup, before anything is written to the database; the caller logs
/// every returned message and skips the write entirely if the list is non-empty.
/// </summary>
public static class RbacDefinitionValidator
{
    /// <summary>Validates the real <see cref="PermissionCategoryCode"/>/<see cref="PermissionGroupCode"/>/
    /// <see cref="PermissionActionCode"/> enums and <see cref="RbacSeedData.Permissions"/>. Empty means valid.</summary>
    public static IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        ValidateEnumValues<PermissionCategoryCode>(errors);
        ValidateEnumValues<PermissionGroupCode>(errors);
        ValidateEnumValues<PermissionActionCode>(errors);

        ValidateDisplayNames<PermissionCategoryCode>(errors, c => c.GetDisplayName());
        ValidateDisplayNames<PermissionGroupCode>(errors, g => g.GetDisplayName());
        ValidateDisplayNames<PermissionActionCode>(errors, a => a.GetDisplayName());

        ValidateIds<PermissionCategoryCode>(errors, c => c.GetId());
        ValidateIds<PermissionGroupCode>(errors, g => g.GetId());

        ValidatePermissionSeeds(errors, RbacSeedData.Permissions);

        return errors;
    }

    /// <summary>Flags two members of <typeparamref name="TEnum"/> sharing the same underlying int value --
    /// C# allows this, but it means <c>.ToString()</c>/<c>(int)</c> conversions (and everything
    /// <see cref="PermissionTaxonomy"/> derives from them) can no longer tell the two members apart. Walks
    /// the type's declared fields rather than <see cref="Enum.GetValues{TEnum}"/>: once two members share a
    /// value, every value in that array already renders as the same (first-declared) name via
    /// <c>ToString()</c>, so the member names themselves would be unrecoverable from the values alone.</summary>
    public static void ValidateEnumValues<TEnum>(List<string> errors)
        where TEnum : struct, Enum
    {
        var enumType = typeof(TEnum);

        foreach (var duplicate in enumType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .GroupBy(f => Convert.ToInt32(f.GetRawConstantValue()))
            .Where(g => g.Count() > 1))
        {
            errors.Add(
                $"{enumType.Name} has duplicate underlying value {duplicate.Key} shared by: " +
                string.Join(", ", duplicate.Select(f => f.Name)));
        }
    }

    /// <summary>Flags two members of <typeparamref name="TEnum"/> with the same display name (case-insensitive)
    /// -- not a data-corruption risk like the other checks, but almost always a copy-paste mistake that leaves
    /// two different permissions indistinguishable in the permission-management UI.</summary>
    public static void ValidateDisplayNames<TEnum>(List<string> errors, Func<TEnum, string> getDisplayName)
        where TEnum : struct, Enum
    {
        var enumName = typeof(TEnum).Name;

        foreach (var duplicate in Enum.GetValues<TEnum>()
            .GroupBy(getDisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            errors.Add(
                $"{enumName} has duplicate display name \"{duplicate.Key}\" shared by: " +
                string.Join(", ", duplicate));
        }
    }

    /// <summary>Flags two members of <typeparamref name="TEnum"/> sharing the same declared Id -- e.g. a
    /// copy-pasted attribute where the Guid literal wasn't changed for the new member. Left undetected, the
    /// bootstrapper would treat both members as the same database row.</summary>
    public static void ValidateIds<TEnum>(List<string> errors, Func<TEnum, Guid> getId)
        where TEnum : struct, Enum
    {
        var enumName = typeof(TEnum).Name;

        foreach (var duplicate in Enum.GetValues<TEnum>()
            .GroupBy(getId)
            .Where(g => g.Count() > 1))
        {
            errors.Add(
                $"{enumName} has duplicate Id {duplicate.Key} shared by: " +
                string.Join(", ", duplicate));
        }
    }

    /// <summary>Flags two <see cref="RbacSeedData.PermissionSeed"/> entries declaring the same (Group, Action)
    /// pair -- Id/Name/Code are all derived from that pair (see <see cref="RbacSeedData.PermissionSeed"/>), so
    /// a duplicate pair means a duplicate permission code the bootstrapper can't tell apart.</summary>
    public static void ValidatePermissionSeeds(
        List<string> errors,
        IReadOnlyCollection<RbacSeedData.PermissionSeed> permissions)
    {
        foreach (var duplicate in permissions
            .GroupBy(p => (p.Group, p.Action))
            .Where(g => g.Count() > 1))
        {
            errors.Add(
                $"RbacSeedData.Permissions declares ({duplicate.Key.Group}, {duplicate.Key.Action}) more than " +
                $"once, producing duplicate code \"{PermissionTaxonomy.BuildPermissionCode(duplicate.Key.Group, duplicate.Key.Action)}\".");
        }
    }
}
