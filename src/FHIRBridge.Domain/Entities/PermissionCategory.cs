using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class PermissionCategory : AuditableChildEntity<Guid>
{
    private PermissionCategory()
    {
    }

    public PermissionCategory(Guid id, string name, string? description = null)
    {
        Id = id;
        Name = name;
        Description = description;
    }

    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }

    public void Update(string name, string? description)
    {
        Name = name;
        Description = description;
    }
}
