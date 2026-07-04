using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class Permission : AuditableChildEntity<Guid>
{
    private Permission()
    {
    }

    public Permission(Guid id, string name, string description, Guid? categoryId = null, bool isSystem = true)
    {
        Id = id;
        Name = name;
        Description = description;
        CategoryId = categoryId;
        IsSystem = isSystem;
    }

    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = default!;

    /// <summary>Logical grouping for the permission (e.g. Configuration, Audit, Workflow); null = ungrouped.</summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>True for seeded built-in permissions; blocks edit/delete.</summary>
    public bool IsSystem { get; private set; }
}
