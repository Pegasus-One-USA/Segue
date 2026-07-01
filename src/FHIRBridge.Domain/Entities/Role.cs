using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class Role : AuditableChildEntity<Guid>
{
    private Role()
    {
    }

    public Role(Guid id, string name, string description, bool isSystem = false, Guid? tenantId = null)
    {
        Id = id;
        Name = name;
        Description = description;
        IsSystem = isSystem;
        TenantId = tenantId;
        IsEnabled = true;
    }

    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = default!;

    /// <summary>Owning tenant for a custom role; null for the global, built-in roles.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>True for the seeded built-in roles; blocks edit/delete of platform roles.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>Whether the role is active and grants its permissions.</summary>
    public bool IsEnabled { get; private set; }

    public void Update(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
