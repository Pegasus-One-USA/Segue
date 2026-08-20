using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class Permission : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private Permission()
    {
    }

    public Permission(Guid id, string name, string displayName, string description, Guid? groupId = null, bool isSystem = true, bool isVisible = true, bool isActive = true, string? instances = null, PermissionSource source = PermissionSource.Code)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        Description = description;
        GroupId = groupId;
        IsSystem = isSystem;
        IsVisible = isVisible;
        IsActive = isActive;
        Instances = instances;
        Source = source;
    }

    /// <summary>Wire-format code (e.g. "user.invite") used for authorization policies; never shown to users.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Human-readable label for permission-management UI (e.g. "Invite User").</summary>
    public string DisplayName { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => DisplayName;

    public string Description { get; private set; } = default!;

    /// <summary>The <see cref="PermissionGroup"/> this permission belongs to; null = ungrouped.</summary>
    public Guid? GroupId { get; private set; }

    /// <summary>True for seeded built-in permissions; blocks edit/delete.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>Whether this permission should be shown in permission-management UI.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>False once the permission is no longer declared in code. Never deleted — existing
    /// PermissionAllocations referencing it (audit history, past role grants) stay intact, but it can no
    /// longer be newly granted.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Every "ClassName.MethodName" (or "ClassName" for a class-level attribute) where a
    /// <c>[StandardPermission]</c> attribute declares this permission, comma-separated. Null if this
    /// permission was never discovered via an attribute (e.g. seeded but not yet enforced anywhere).</summary>
    public string? Instances { get; private set; }

    /// <summary>Where this row's existence is owned/governed from — see <see cref="PermissionSource"/>'s own
    /// doc comment. Never consulted by runtime authorization (<c>PermissionAuthorizationHandler</c>); only
    /// the boot-time sync in <c>Program.SyncDiscoveredPermissionsAsync</c> reads this, to decide whether a
    /// permission no longer backed by code is safe to auto-deactivate.</summary>
    public PermissionSource Source { get; private set; }

    public void UpdateDisplayName(string displayName)
    {
        DisplayName = displayName;
    }

    public void UpdateName(string name)
    {
        Name = name;
    }

    public void UpdateDescription(string description)
    {
        Description = description;
    }

    public void UpdateInstances(string? instances)
    {
        Instances = instances;
    }

    public void Activate()
    {
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
    }

    /// <summary>
    /// Promotes this row to code-governed — called when code starts declaring/discovering a permission that
    /// previously only existed via a hand-authored migration (<see cref="PermissionSource.Migration"/>). A
    /// one-way transition: once code is responsible for a permission, it stays responsible, including for
    /// eventually deactivating it if the declaration/discovery is later removed.
    /// </summary>
    public void MarkAsCodeManaged()
    {
        Source = PermissionSource.Code;
    }

    /// <summary>Re-parents this permission under a different <see cref="PermissionGroup"/>. The permission keeps
    /// its own Id, so every PermissionAllocation referencing it is unaffected.</summary>
    public void UpdateGroup(Guid groupId)
    {
        GroupId = groupId;
    }
}
