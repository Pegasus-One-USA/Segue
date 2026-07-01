using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Terminology;

/// <summary>
/// Validates that a code is a member of a ValueSet (FHIR <c>ValueSet/$validate-code</c>), used to enforce US Core
/// required code bindings. Returns null when membership cannot be determined (no local seed and no server configured)
/// so callers can skip rather than emit a false negative.
/// </summary>
public interface ITerminologyValidationService
{
    Task<TerminologyValidationResult?> ValidateCodeAsync(
        string valueSetUrl,
        string? system,
        string code,
        CancellationToken cancellationToken);
}
