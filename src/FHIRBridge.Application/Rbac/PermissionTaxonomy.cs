using System.Reflection;

namespace FHIRBridge.Application.Security;

/// <summary>
/// Single source of truth for how the standardized permission enums fit together: resolves each
/// <see cref="PermissionCategoryCode"/>/<see cref="PermissionGroupCode"/> member's stable Id, display name,
/// and (for groups) owning category straight from the attribute declared on that enum member — see
/// <see cref="PermissionCategoryAttribute"/> and <see cref="PermissionGroupAttribute"/>. Also renders a
/// group/action pair into its wire-format permission code (e.g. <c>"user.view"</c>) and derives its id.
/// </summary>
public static class PermissionTaxonomy
{
    /// <summary>Builds the wire-format permission code, e.g. <c>(User, View)</c> -&gt; <c>"user.view"</c>.</summary>
    public static string BuildPermissionCode(PermissionGroupCode group, PermissionActionCode action)
    {
        return $"{group.ToString().ToLowerInvariant()}.{action.ToString().ToLowerInvariant()}";
    }

    /// <summary>Builds the human-readable display name, e.g. <c>(User, Invite)</c> -&gt; <c>"Invite User"</c>.</summary>
    public static string BuildPermissionDisplayName(PermissionGroupCode group, PermissionActionCode action)
    {
        return $"{action.GetDisplayName()} {group.GetDisplayName()}";
    }

    /// <summary>
    /// Deterministic id for a permission, packing its Group+Action pair into a stable, human-readable GUID —
    /// group occupies the first 8 digits, action the last 12 — e.g. group=10, action=11 renders as
    /// <c>00000010-0000-0000-0000-000000000011</c>. Formatted with "D" (decimal), not "X" (hex): decimal digits
    /// 0-9 are always valid hex characters too, so the string is still a well-formed Guid, but every digit
    /// reads back as the enum's literal int value — no digit ever turns into a letter (e.g. group=10 stays
    /// "10", never hex 'a') the way it would past 9 with hex formatting. Category is deliberately excluded:
    /// it's a mutable fact about the group (a group's <see cref="PermissionGroupAttribute.Category"/> can be
    /// re-pointed, see <c>RbacBootstrapper</c>), not part of the permission's identity, so baking it into the
    /// id would go stale the moment that mapping changes. The same <see cref="PermissionActionCode"/> reused
    /// under a different <see cref="PermissionGroupCode"/> (e.g. View under both User and Role) naturally
    /// produces two different ids since the group digits differ — nobody has to hand-pick a GUID for a new
    /// permission. Every enum member here must start at 1; 0 is reserved to mean "no group"/"no action" and
    /// is never a real value.
    /// </summary>
    public static Guid BuildPermissionId(PermissionGroupCode group, PermissionActionCode action)
    {
        return Guid.Parse($"{(int)group:D8}-0000-0000-0000-{(int)action:D12}");
    }

    /// <summary>Resolves a <see cref="PermissionCategoryCode"/>'s stable seeded Id from its <see cref="PermissionCategoryAttribute"/>.</summary>
    public static Guid GetId(this PermissionCategoryCode category)
    {
        return GetCategoryAttribute(category).Id;
    }

    /// <summary>Resolves a <see cref="PermissionCategoryCode"/>'s <see cref="PermissionCategoryAttribute"/> display text.</summary>
    public static string GetDisplayName(this PermissionCategoryCode category)
    {
        return GetCategoryAttribute(category).DisplayName;
    }

    /// <summary>Resolves a <see cref="PermissionGroupCode"/>'s stable seeded Id from its <see cref="PermissionGroupAttribute"/>.</summary>
    public static Guid GetId(this PermissionGroupCode group)
    {
        return GetGroupAttribute(group).Id;
    }

    /// <summary>Resolves a <see cref="PermissionGroupCode"/>'s <see cref="PermissionGroupAttribute"/> display text.</summary>
    public static string GetDisplayName(this PermissionGroupCode group)
    {
        return GetGroupAttribute(group).DisplayName;
    }

    /// <summary>Resolves the <see cref="PermissionCategoryCode"/> that owns a <see cref="PermissionGroupCode"/>.</summary>
    public static PermissionCategoryCode GetCategory(this PermissionGroupCode group)
    {
        return GetGroupAttribute(group).Category;
    }

    /// <summary>Resolves a <see cref="PermissionActionCode"/>'s <see cref="PermissionDisplayNameAttribute"/> text.</summary>
    public static string GetDisplayName(this PermissionActionCode action)
    {
        var field = typeof(PermissionActionCode).GetField(action.ToString())!;
        var attribute = field.GetCustomAttribute<PermissionDisplayNameAttribute>();

        return attribute?.DisplayName ?? action.ToString();
    }

    private static PermissionCategoryAttribute GetCategoryAttribute(PermissionCategoryCode category)
    {
        var field = typeof(PermissionCategoryCode).GetField(category.ToString())!;

        return field.GetCustomAttribute<PermissionCategoryAttribute>()
            ?? throw new InvalidOperationException(
                $"PermissionCategoryCode.{category} is missing a [PermissionCategory(id, displayName)] attribute.");
    }

    private static PermissionGroupAttribute GetGroupAttribute(PermissionGroupCode group)
    {
        var field = typeof(PermissionGroupCode).GetField(group.ToString())!;

        return field.GetCustomAttribute<PermissionGroupAttribute>()
            ?? throw new InvalidOperationException(
                $"PermissionGroupCode.{group} is missing a [PermissionGroup(id, category, displayName)] attribute.");
    }
}
