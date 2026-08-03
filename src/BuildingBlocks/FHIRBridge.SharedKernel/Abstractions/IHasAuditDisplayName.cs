namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Optional human-readable label for an <see cref="IAuditableEntity"/>, shown in the audit trail instead of
/// its raw primary key. Implemented only by entities that have an obvious single configured name (a
/// connection's Name, a role's Name, etc.) — entities without one simply don't implement this, and the audit
/// trail falls back to showing the raw id as before.
/// </summary>
public interface IHasAuditDisplayName
{
    string? AuditDisplayName { get; }
}
