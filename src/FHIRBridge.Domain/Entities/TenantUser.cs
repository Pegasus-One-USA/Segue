using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class TenantUser : AuditableChildEntity<Guid>
{
    private TenantUser()
    {
    }

    public TenantUser(Guid tenantId, Guid userId, Guid roleId)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        UserId = userId;
        RoleId = roleId;
        IsEnabled = true;
    }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }

    /// <summary>Membership enable/disable. When false the membership is suspended (renamed from IsActive).</summary>
    public bool IsEnabled { get; private set; }

    public void UpdateRole(Guid roleId)
    {
        RoleId = roleId;
    }

    public void Activate()
    {
        IsEnabled = true;
    }

    public void Deactivate()
    {
        IsEnabled = false;
    }
}
