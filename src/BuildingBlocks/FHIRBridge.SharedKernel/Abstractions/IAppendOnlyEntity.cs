namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Marks an entity as append-only: rows may only be inserted, never modified or deleted.
/// <see cref="AuditableEntity{TId}"/> handles soft-delete instead; this is for genuine
/// immutable log records (audit trail, authentication log, data access log) where HIPAA/SOC2
/// audit-control requirements demand tamper-evidence rather than reversible deletion.
/// Enforced centrally by the auditing SaveChanges interceptor.
/// </summary>
public interface IAppendOnlyEntity
{
}
