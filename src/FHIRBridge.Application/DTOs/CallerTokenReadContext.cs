namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Reusable input for any read that uses a caller-supplied (pass-through) EHR access token: the token the caller's
/// app already obtained from its own SMART login, the FHIR base URL it is valid for, the target patient, and which
/// EHR connector to use. FHIRBridge uses the token transiently and stores nothing. Any future caller-held-token
/// feature (a different resource shape, a bulk pull, another vendor) can reuse this context + the read service.
/// </summary>
public sealed record CallerTokenReadContext(
    string Ehr,
    string FhirBaseUrl,
    string AccessToken,
    string PatientId);
