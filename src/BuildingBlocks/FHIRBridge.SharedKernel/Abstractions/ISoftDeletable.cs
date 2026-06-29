namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Marks an entity that is never physically deleted: a delete flips <see cref="IsDeleted"/> instead, so
/// historical audit/lineage references to the row's id stay resolvable. A global EF query filter hides
/// soft-deleted rows from normal queries; the <see cref="AuditingSaveChangesInterceptor"/> converts
/// delete operations into soft deletes and stamps the provenance.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTime? DeletedOnUtc { get; }
    string? DeletedBy { get; }

    void ApplyDeleted(string? userId, DateTime utcNow);
}
