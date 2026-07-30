namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>
/// Result of completing the authorization-code (+ PKCE) round-trip — the access token plus the diagnostic facts a
/// caller needs to tell Governance whether this sign-in actually established the context later searches depend on:
/// the scope Epic actually granted (vs. what was requested), whether a <c>patient</c> claim came back (the thing a
/// Patient/EHR-launch flow needs for unscoped <c>Patient</c> searches to succeed), and the token-cache key this
/// session was saved under (so a later lookup-time key can be compared against it without ever printing the
/// underlying CallerId/session identifier). <see cref="PatientId"/> and <see cref="PractitionerId"/> carry the raw
/// FHIR resource ids a user-to-FHIR-context binding enforces against: <see cref="PatientId"/> is the token
/// response's <c>patient</c> claim (Patient/EHR-launch flows); <see cref="PractitionerId"/> is parsed from the
/// id_token's <c>fhirUser</c> claim when it references a Practitioner (Provider Standalone).
/// </summary>
public sealed record SmartAuthorizationCodeExchangeResult(
    string AccessToken,
    string? GrantedScope,
    bool PatientContextGranted,
    string TokenCacheKeyHash,
    string? PatientId = null,
    string? PractitionerId = null);
