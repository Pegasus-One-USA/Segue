using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class Permission : AuditableChildEntity<Guid>
{
    private Permission()
    {
    }

    public Permission(Guid id, string name, string displayName, string description, Guid? groupId = null, bool isSystem = true, bool isVisible = true)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        Description = description;
        GroupId = groupId;
        IsSystem = isSystem;
        IsVisible = isVisible;
    }

    /// <summary>Wire-format code (e.g. "user.invite") used for authorization policies; never shown to users.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Human-readable label for permission-management UI (e.g. "Invite User").</summary>
    public string DisplayName { get; private set; } = default!;

    public string Description { get; private set; } = default!;

    /// <summary>The <see cref="PermissionGroup"/> this permission belongs to; null = ungrouped.</summary>
    public Guid? GroupId { get; private set; }

    /// <summary>True for seeded built-in permissions; blocks edit/delete.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>Whether this permission should be shown in permission-management UI.</summary>
    public bool IsVisible { get; private set; }

    public void UpdateDisplayName(string displayName)
    {
        DisplayName = displayName;
    }
}
