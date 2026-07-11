namespace FHIRBridge.Application.Security;

/// <summary>
/// Human-readable label for a <see cref="PermissionActionCode"/> member, used to build UI-facing display
/// text without exposing the enum's own code-oriented member name (e.g. "Audit Logs" instead of "AuditLogs").
/// <see cref="PermissionCategoryCode"/> and <see cref="PermissionGroupCode"/> carry their display name on
/// their own dedicated attributes (<see cref="PermissionCategoryAttribute"/>/<see cref="PermissionGroupAttribute"/>)
/// instead, since those also need to carry a stable Id.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class PermissionDisplayNameAttribute : Attribute
{
    public PermissionDisplayNameAttribute(string displayName)
    {
        DisplayName = displayName;
    }

    public string DisplayName { get; }
}
