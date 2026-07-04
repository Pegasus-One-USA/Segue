using System.Reflection;

namespace FHIRBridge.Application.Security;

/// <summary>
/// Single source of truth for how the standardized permission enums fit together: which
/// <see cref="PermissionCategoryCode"/> owns each <see cref="PermissionGroupCode"/>, how a
/// group/action pair is rendered into the wire-format permission code (e.g. <c>"user.view"</c>), and how
/// each enum member's <see cref="PermissionDisplayNameAttribute"/> is resolved into human-readable text.
/// </summary>
public static class PermissionTaxonomy
{
    public static readonly IReadOnlyDictionary<PermissionGroupCode, PermissionCategoryCode> GroupCategory =
        new Dictionary<PermissionGroupCode, PermissionCategoryCode>
        {
            [PermissionGroupCode.User] = PermissionCategoryCode.AccessControl,
            [PermissionGroupCode.Role] = PermissionCategoryCode.AccessControl,
            [PermissionGroupCode.Configuration] = PermissionCategoryCode.Platform,
            [PermissionGroupCode.Pipeline] = PermissionCategoryCode.Platform,
            [PermissionGroupCode.AuditLogs] = PermissionCategoryCode.Platform,
            [PermissionGroupCode.SourceConnections] = PermissionCategoryCode.Platform,
            [PermissionGroupCode.Workflow] = PermissionCategoryCode.Platform,
            [PermissionGroupCode.Report] = PermissionCategoryCode.Platform,
            [PermissionGroupCode.Payload] = PermissionCategoryCode.Platform,
        };

    /// <summary>Builds the wire-format permission code, e.g. <c>(User, View)</c> -&gt; <c>"user.view"</c>.</summary>
    public static string BuildPermissionCode(PermissionGroupCode group, PermissionActionCode action) =>
        $"{group.ToString().ToLowerInvariant()}.{action.ToString().ToLowerInvariant()}";

    /// <summary>Builds the human-readable display name, e.g. <c>(User, Invite)</c> -&gt; <c>"Invite User"</c>.</summary>
    public static string BuildPermissionDisplayName(PermissionGroupCode group, PermissionActionCode action) =>
        $"{action.GetDisplayName()} {group.GetDisplayName()}";

    // Arbitrary fixed anchor for permission-id generation — its value doesn't matter, only that it never
    // changes, since every permission id is derived from it plus the permission's own wire-format code.
    private static readonly Guid PermissionIdNamespace = new("6f8f1f6a-2b6a-4b3e-9c9a-2b6f6f1f6a2b");

    /// <summary>
    /// Deterministic id for a permission, derived from its Group+Action pair (the same pair the same wire-format
    /// code comes from). The same permission always gets the same id in every environment without anyone having
    /// to hand-pick a GUID for it — including the same <see cref="PermissionActionCode"/> reused under a
    /// different <see cref="PermissionGroupCode"/> (e.g. View under both User and Role), which naturally
    /// produces two different ids since the full code differs ("user.view" vs "role.view").
    /// </summary>
    public static Guid BuildPermissionId(PermissionGroupCode group, PermissionActionCode action) =>
        DeterministicGuid.Create(PermissionIdNamespace, BuildPermissionCode(group, action));

    /// <summary>Resolves a <see cref="PermissionCategoryCode"/>'s <see cref="PermissionDisplayNameAttribute"/> text.</summary>
    public static string GetDisplayName(this PermissionCategoryCode category) => GetDisplayNameCore(category);

    /// <summary>Resolves a <see cref="PermissionGroupCode"/>'s <see cref="PermissionDisplayNameAttribute"/> text.</summary>
    public static string GetDisplayName(this PermissionGroupCode group) => GetDisplayNameCore(group);

    /// <summary>Resolves a <see cref="PermissionActionCode"/>'s <see cref="PermissionDisplayNameAttribute"/> text.</summary>
    public static string GetDisplayName(this PermissionActionCode action) => GetDisplayNameCore(action);

    private static string GetDisplayNameCore<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var field = typeof(TEnum).GetField(value.ToString())!;
        var attribute = field.GetCustomAttribute<PermissionDisplayNameAttribute>();

        return attribute?.DisplayName ?? value.ToString();
    }
}
