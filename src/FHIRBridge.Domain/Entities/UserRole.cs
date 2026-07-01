namespace FHIRBridge.Domain.Entities;

public sealed class UserRole
{
    private UserRole()
    {
    }

    public UserRole(Guid userId, Guid roleId)
    {
        UserId = userId;
        RoleId = roleId;
        IsEnabled = true;
    }

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }

    /// <summary>Soft-disables a global role assignment without removing the row.</summary>
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
