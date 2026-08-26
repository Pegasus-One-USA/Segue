using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A customer/company using this deployment — the real multi-tenant isolation boundary this codebase
/// previously lacked entirely (there was no Tenant entity, no TenantId on <see cref="User"/>, and the
/// portal's "orgId" was a hardcoded placeholder identical for every user). Every <see cref="User"/> belongs
/// to exactly one Tenant; every <see cref="BrandConfiguration"/> belongs to exactly one Tenant.
///
/// Matches the shape the product's own (previously mock-only) Tenant Management UI already used —
/// Id/Name/Code/CreatedAt — so this entity is the real backend for that screen, not a parallel concept.
/// </summary>
public sealed class Tenant : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private Tenant()
    {
    }

    // Takes an explicit id (mirrors Role's constructor convention) rather than always generating one — the
    // migration/seed path needs to create the well-known Default Tenant with a fixed id
    // (SeededSecurityIds.DefaultTenantId), not a random one.
    public Tenant(Guid id, string name, string code)
    {
        Id = id;
        Name = name;
        Code = code;
        IsActive = true;
    }

    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;

    /// <summary>URL-safe slug (letters/digits/hyphen/underscore) — used for pre-login branding resolution
    /// via <c>/auth/login?tenant=&lt;code&gt;</c>. Unique, case-insensitively.</summary>
    public string Code { get; private set; } = default!;

    /// <summary>Deactivating a tenant (as opposed to deleting it) keeps its users/branding/history intact
    /// but is available for a future "suspend a customer" flow to key off — not yet enforced anywhere.</summary>
    public bool IsActive { get; private set; } = true;

    public void Update(string name, string code)
    {
        Name = name;
        Code = code;
    }

    public void SetActive(bool isActive)
    {
        IsActive = isActive;
    }
}
