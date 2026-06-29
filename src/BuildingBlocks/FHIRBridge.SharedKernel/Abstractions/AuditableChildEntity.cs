namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Non-aggregate entity (e.g. a member of an aggregate, or an RBAC row) that still carries full
/// creation/modification provenance, soft-delete state, and an optimistic concurrency token. Mirrors
/// <see cref="AuditableEntity{TId}"/> without the domain-event machinery of an aggregate root.
/// </summary>
public abstract class AuditableChildEntity<TId> : Entity<TId>, IAuditableEntity, ISoftDeletable
{
    public DateTime CreatedOnUtc { get; protected set; } = DateTime.UtcNow;
    public string? CreatedBy { get; protected set; }
    public DateTime? ModifiedOnUtc { get; protected set; }
    public string? ModifiedBy { get; protected set; }

    public bool IsDeleted { get; protected set; }
    public DateTime? DeletedOnUtc { get; protected set; }
    public string? DeletedBy { get; protected set; }

    /// <summary>Optimistic-concurrency token (mapped as a SQL <c>rowversion</c>).</summary>
    public byte[] RowVersion { get; protected set; } = [];

    public void ApplyCreated(string? userId, DateTime utcNow)
    {
        CreatedBy = userId;
        CreatedOnUtc = utcNow;
    }

    public void ApplyModified(string? userId, DateTime utcNow)
    {
        ModifiedBy = userId;
        ModifiedOnUtc = utcNow;
    }

    public void ApplyDeleted(string? userId, DateTime utcNow)
    {
        IsDeleted = true;
        DeletedBy = userId;
        DeletedOnUtc = utcNow;
    }
}
