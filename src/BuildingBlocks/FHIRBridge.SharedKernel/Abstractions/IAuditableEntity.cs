namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Marks an entity that records creation/modification provenance (who + when). The
/// <see cref="AuditingSaveChangesInterceptor"/> stamps these on insert/update so individual
/// services never have to set them by hand.
/// </summary>
public interface IAuditableEntity
{
    DateTime CreatedOnUtc { get; }
    string? CreatedBy { get; }
    DateTime? ModifiedOnUtc { get; }
    string? ModifiedBy { get; }

    void ApplyCreated(string? userId, DateTime utcNow);
    void ApplyModified(string? userId, DateTime utcNow);
}
