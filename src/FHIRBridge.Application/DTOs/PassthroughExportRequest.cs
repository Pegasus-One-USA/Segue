namespace FHIRBridge.Application.DTOs;

/// <summary>
/// The request the third-party provider app posts for a stateless, caller-token export: it supplies the Epic access
/// token it obtained from its OWN SMART login, plus the FHIR base URL and the patient the clinician selected.
/// FHIRBridge uses the token once, fetches the data, and returns it — storing nothing.
/// </summary>
/// <param name="AccessToken">The EHR bearer token the app is holding.</param>
/// <param name="FhirBaseUrl">The FHIR R4 base URL the token is valid for.</param>
/// <param name="PatientId">The logical id of the patient the clinician selected.</param>
/// <param name="Ehr">Which connector to use (vendor quirks). Epic first; defaults to "epic".</param>
/// <param name="Include">Compartment selection: "all" (default) or a CSV like "Observation,Condition".</param>
/// <param name="DestinationId">Which admin-configured output/format to produce; null uses the tenant default.</param>
public sealed record PassthroughExportRequest(
    string AccessToken,
    string FhirBaseUrl,
    string PatientId,
    string Ehr = "epic",
    string? Include = null,
    Guid? DestinationId = null);
