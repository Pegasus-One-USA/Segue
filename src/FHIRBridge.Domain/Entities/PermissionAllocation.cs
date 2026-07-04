using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// Grants (or explicitly denies) a permission to either a Role or a User — exactly one of
/// <see cref="RoleId"/>/<see cref="UserId"/> is set. Role-level rows are the built-in RBAC grants;
/// user-level rows are direct overrides that take precedence over whatever the user's roles imply.
/// </summary>
public sealed class PermissionAllocation : AuditableChildEntity<Guid>
{
    private PermissionAllocation()
    {
    }

    private PermissionAllocation(Guid id, Guid? roleId, Guid? userId, Guid permissionId, bool isEnabled)
    {
        if (roleId is null == userId is null)
        {
            throw new ArgumentException("Exactly one of roleId or userId must be set.");
        }

        Id = id;
        RoleId = roleId;
        UserId = userId;
        PermissionId = permissionId;
        IsEnabled = isEnabled;
    }

    public static PermissionAllocation ForRole(Guid id, Guid roleId, Guid permissionId, bool isEnabled = true)
        => new(id, roleId, null, permissionId, isEnabled);

    public static PermissionAllocation ForUser(Guid id, Guid userId, Guid permissionId, bool isEnabled)
        => new(id, null, userId, permissionId, isEnabled);

    public Guid? RoleId { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid PermissionId { get; private set; }

    /// <summary>For a role-level row: the grant is active. For a user-level row: explicit grant (true) or explicit deny (false).</summary>
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
