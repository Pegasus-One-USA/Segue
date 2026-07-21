using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class Role : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private Role()
    {
    }

    public Role(
        Guid id,
        string name,
        string description,
        bool isSystem = false,
        bool isDefault = false)
    {
        Id = id;
        Name = name;
        Description = description;
        IsSystem = isSystem;
        IsEnabled = true;
        IsDefault = isDefault;
    }

    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;
    public string Description { get; private set; } = default!;

    /// <summary>True for the seeded built-in roles; blocks edit/delete of platform roles.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>True for the auto-generated default roles.</summary>
    public bool IsDefault { get; private set; }

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
