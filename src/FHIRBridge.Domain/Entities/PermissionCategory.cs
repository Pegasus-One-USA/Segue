using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class PermissionCategory : AuditableChildEntity<Guid>
{
    private PermissionCategory()
    {
    }

    public PermissionCategory(Guid id, string name, string displayName, string? description = null, bool isVisible = true)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        Description = description;
        IsVisible = isVisible;
    }

    /// <summary>Stable code identifier, matching the owning <c>PermissionCategoryCode</c> enum member's name.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Human-readable label for permission-management UI (e.g. "Access Control").</summary>
    public string DisplayName { get; private set; } = default!;

    public string? Description { get; private set; }

    /// <summary>Whether this category should be shown in permission-management UI.</summary>
    public bool IsVisible { get; private set; }

    public void UpdateDisplayName(string displayName)
    {
        DisplayName = displayName;
    }

    public void Update(string name, string? description)
    {
        Name = name;
        Description = description;
    }
}
