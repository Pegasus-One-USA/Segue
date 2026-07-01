namespace FHIRBridge.Domain.Entities;

public sealed class RolePermission
{
    private RolePermission()
    {
    }

    public RolePermission(Guid roleId, Guid permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
        IsEnabled = true;
    }

    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }

    /// <summary>Soft-disables a single role→permission grant without removing the row.</summary>
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
