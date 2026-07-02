using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Aggregation;

/// <summary>
/// Stateless, caller-token patient read: fetches the Patient plus the requested compartment types using an access
/// token supplied by the caller (their app owns the SMART login), best-effort, and returns the merged result. It
/// resolves NO stored source connection, acquires NO token, and persists nothing. Semantics mirror
/// <c>IPatientAggregationService</c>; the only difference is where the token comes from.
/// </summary>
public interface IPassthroughPatientReadService
{
    /// <summary>
    /// Retrieves the Patient (<c>Patient?_id={patientId}</c>) and each compartment type in
    /// <paramref name="resourceTypes"/> (<c>{Type}?patient={patientId}</c>) from the FHIR base URL in
    /// <paramref name="context"/>, authenticating with the caller-supplied token. Best-effort: a type whose query
    /// fails is reported as a failure rather than throwing.
    /// </summary>
    Task<PatientAggregationResult> GetEverythingAsync(
        CallerTokenReadContext context,
        IReadOnlyCollection<string> resourceTypes,
        CancellationToken cancellationToken);
}
