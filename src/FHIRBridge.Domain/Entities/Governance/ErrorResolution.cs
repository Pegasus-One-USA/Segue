using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Mutable triage/resolution state for a captured <see cref="ErrorLog"/>, keyed by its
/// <see cref="ErrorReferenceId"/>. Deliberately NOT append-only (mirrors <c>SecurityEvent</c>): a support
/// engineer marks an error Resolved, which must update in place. Kept separate from <see cref="ErrorLog"/>
/// so the forensic error record itself stays immutable (HIPAA/SOC2). Absence of a row means status "Open".
/// </summary>
public sealed class ErrorResolution : Entity<Guid>
{
    public const string StatusOpen = "Open";
    public const string StatusResolved = "Resolved";

    private ErrorResolution()
    {
    }

    public ErrorResolution(Guid id, string errorReferenceId)
    {
        Id = id;
        ErrorReferenceId = errorReferenceId;
        Status = StatusOpen;
    }

    public string ErrorReferenceId { get; private set; } = default!;
    public string Status { get; private set; } = StatusOpen;
    public string? ResolvedBy { get; private set; }
    public DateTime? ResolvedOnUtc { get; private set; }
    public string? Notes { get; private set; }

    public void Resolve(string resolvedBy, DateTime resolvedOnUtc, string? notes)
    {
        Status = StatusResolved;
        ResolvedBy = resolvedBy;
        ResolvedOnUtc = resolvedOnUtc;
        Notes = notes;
    }

    public void Reopen()
    {
        Status = StatusOpen;
        ResolvedBy = null;
        ResolvedOnUtc = null;
    }
}
