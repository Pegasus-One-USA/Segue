namespace FHIRBridge.Application.Abstractions.Aggregation;

/// <summary>
/// Synchronous, patient-scoped FHIR aggregation read. Fetches the Patient plus a caller-selected set of
/// patient-compartment resource types from the tenant's source, fans the queries out in parallel, fully paginates
/// each, and returns the merged result. This is a pass-through read: NO mapping, NO destination, NO PipelineRun.
/// </summary>
public interface IPatientAggregationService
{
    /// <summary>
    /// Retrieves the Patient (<c>Patient?_id={patientId}</c>) and each compartment type in
    /// <paramref name="resourceTypes"/> (<c>{Type}?patient={patientId}</c>) from the tenant's source connection,
    /// best-effort: a type whose query fails is reported as a failure rather than throwing.
    /// </summary>
    /// <param name="resourceTypes">
    /// The compartment resource types to query (Patient excluded — the root is always read). Typically the output
    /// of <c>PatientCompartmentResolver.Resolve</c>.
    /// </param>
    /// <param name="sourceConnectionId">
    /// Optional explicit source connection to read from. When null, the tenant's single enabled source is used;
    /// if the tenant has zero or more than one enabled source, resolution fails with a typed error.
    /// </param>
    Task<PatientAggregationResult> GetEverythingAsync(
        Guid tenantId,
        string patientId,
        IReadOnlyCollection<string> resourceTypes,
        Guid? sourceConnectionId,
        CancellationToken cancellationToken);
}
