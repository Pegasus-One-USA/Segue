namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Phase 6A – manages the mutable Open/Resolved triage state of a captured error, keyed by its
/// ErrorReferenceId. Kept separate from the immutable ErrorLog record so the forensic entry is never
/// mutated. Returns false when the reference id is unknown.
/// </summary>
public interface IErrorResolutionService
{
    Task<bool> ResolveAsync(string errorReferenceId, string resolvedBy, string? notes, CancellationToken cancellationToken);

    Task<bool> ReopenAsync(string errorReferenceId, CancellationToken cancellationToken);
}
