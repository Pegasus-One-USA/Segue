namespace FHIRBridge.Application.Security;

/// <summary>
/// Resolves the <see cref="PermissionGroupCode"/> that governs a resource-based permission check for
/// a given enum value (e.g. a <c>SourceSystemType</c>) — by name, not a hand-maintained lookup table.
/// A value gets its own dedicated group (and, via <see cref="PermissionCatalog"/>, its own
/// auto-discovered permissions) purely by there being a same-named <see cref="PermissionGroupCode"/>
/// member; a value with no matching group name falls back to the generic
/// <see cref="PermissionGroupCode.SourceConnections"/> group. This is why every vendor
/// <see cref="PermissionGroupCode"/> member (Epic, Athenahealth, Cerner, ...) must be named
/// identically to its source enum's member (e.g. <c>SourceSystemType.Athenahealth</c>).
/// </summary>
public static class SourceSystemPermissionGroups
{
    /// <summary>
    /// The permission group for the given enum value — the dedicated group if
    /// <see cref="PermissionGroupCode"/> has a member with the same name, otherwise the generic
    /// <see cref="PermissionGroupCode.SourceConnections"/> fallback.
    /// </summary>
    public static PermissionGroupCode GroupFor(Enum value)
    {
        // An undefined value of value's own enum type (e.g. a raw out-of-range integer let through by
        // JsonStringEnumConverter's default AllowIntegerValues) renders via ToString() as just its number —
        // Enum.TryParse would then happily parse that number into PermissionGroupCode's own numbering,
        // which can coincide with a real, unrelated vendor's group. Reject it before that can happen.
        if (!Enum.IsDefined(value.GetType(), value))
        {
            return PermissionGroupCode.SourceConnections;
        }

        return Enum.TryParse<PermissionGroupCode>(value.ToString(), ignoreCase: false, out var group)
            ? group
            : PermissionGroupCode.SourceConnections;
    }

    /// <summary>
    /// Every group a dynamic permission check declared against <paramref name="enumType"/> can resolve
    /// to right now — one per value of that enum with a same-named <see cref="PermissionGroupCode"/>
    /// member, resolved through <see cref="GroupFor"/>. Deliberately excludes the generic
    /// <see cref="PermissionGroupCode.SourceConnections"/> fallback: that group isn't tied to one
    /// vendor, so its permissions are curated by hand in <see cref="RbacSeedData.Permissions"/> instead
    /// of being swept into this auto-discovery loop. Adding a new value to <paramref name="enumType"/>,
    /// or a new same-named <see cref="PermissionGroupCode"/> member for an existing one, changes this
    /// set with no other edit — <see cref="PermissionCatalog"/> crosses it with each
    /// <see cref="DynamicSourceSystemPermissionAttribute"/>'s action to auto-discover one permission
    /// per group, the same way it discovers one permission per <see cref="StandardPermissionAttribute"/>.
    /// </summary>
    public static IReadOnlyCollection<PermissionGroupCode> AllGroupsFor(Type enumType)
    {
        return Enum.GetValues(enumType)
            .Cast<Enum>()
            .Select(GroupFor)
            .Where(group => group != PermissionGroupCode.SourceConnections)
            .Distinct()
            .ToArray();
    }
}
