namespace FHIRBridge.Application.Security;

/// <summary>
/// Human-readable label for a <see cref="PermissionCategoryCode"/>, <see cref="PermissionGroupCode"/>, or
/// <see cref="PermissionActionCode"/> member, used to build UI-facing display text without exposing the
/// enum's own code-oriented member name (e.g. "Audit Logs" instead of "AuditLogs").
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
