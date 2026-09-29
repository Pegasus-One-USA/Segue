namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Entity that carries creation/modification provenance and appears in the audit trail, but is still
/// <em>physically</em> deleted — deliberately neither <see cref="ISoftDeletable"/> nor carrying a concurrency
/// token, which is what separates this from <see cref="AuditableChildEntity{TId}"/>.
/// <para>For rows where a delete must genuinely remove the row rather than hide it, while still being recorded.
/// The motivating case is access control: a revoked role assignment that lingers as a soft-deleted row has to be
/// filtered out by every single read, and one that forgets still grants access. <c>AuditingSaveChangesInterceptor</c>
/// has an explicit <c>EntityState.Deleted</c> branch that writes the "Deleted" row for these, capturing the removed
/// state in <c>OldValueJson</c>.</para>
/// </summary>
public abstract class AuditTrackedEntity<TId> : Entity<TId>, IAuditableEntity
{
    public DateTime CreatedOnUtc { get; protected set; } = DateTime.UtcNow;

    /// <summary>
    /// Seeded rather than left null: <c>CreatedBy</c> is mapped NOT NULL for every <see cref="IAuditableEntity"/>
    /// (see FHIRBridgeDbContext's convention), and these rows are written on paths that can legitimately have no
    /// current user — a startup seed, a background job, an authorization callback. The in-memory provider used by
    /// tests enforces the NOT NULL without applying the database default that covers this in production.
    /// <see cref="ApplyCreated"/> overwrites it with the real actor whenever there is one.
    /// </summary>
    public string? CreatedBy { get; protected set; } = "system";
    public DateTime? ModifiedOnUtc { get; protected set; }
    public string? ModifiedBy { get; protected set; }

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
}
