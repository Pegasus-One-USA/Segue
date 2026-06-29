namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Aggregate root that carries full creation/modification provenance, soft-delete state, and an optimistic
/// concurrency token. Provenance and soft-delete are stamped centrally by the auditing interceptor.
/// </summary>
public abstract class AuditableEntity<TId> : AggregateRoot<TId>, IAuditableEntity, ISoftDeletable
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

    public void MarkCreated(string? userId) => ApplyCreated(userId, DateTime.UtcNow);

    public void MarkModified(string? userId) => ApplyModified(userId, DateTime.UtcNow);

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
