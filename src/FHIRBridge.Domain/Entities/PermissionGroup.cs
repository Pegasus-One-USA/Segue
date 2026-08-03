using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class PermissionGroup : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private PermissionGroup()
    {
    }

    public PermissionGroup(Guid id, string name, string displayName, Guid categoryId, string? description = null, bool isVisible = true)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        CategoryId = categoryId;
        Description = description;
        IsVisible = isVisible;
    }

    /// <summary>Stable code identifier, matching the owning <c>PermissionGroupCode</c> enum member's name.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Human-readable label for permission-management UI (e.g. "Audit Logs").</summary>
    public string DisplayName { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => DisplayName;

    public string? Description { get; private set; }

    /// <summary>The <see cref="PermissionCategory"/> this group belongs to.</summary>
    public Guid CategoryId { get; private set; }

    /// <summary>Whether this group should be shown in permission-management UI.</summary>
    public bool IsVisible { get; private set; }

    public void UpdateDisplayName(string displayName)
    {
        DisplayName = displayName;
    }

    /// <summary>Re-parents this group under a different <see cref="PermissionCategory"/>. The group keeps its own
    /// Id, so every Permission/PermissionAllocation referencing it is unaffected.</summary>
    public void UpdateCategory(Guid categoryId)
    {
        CategoryId = categoryId;
    }

    public void Update(string name, string? description)
    {
        Name = name;
        Description = description;
    }
}
