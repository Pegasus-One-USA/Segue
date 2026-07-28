namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>
/// Result of completing the authorization-code (+ PKCE) round-trip — the access token plus the diagnostic facts a
/// caller needs to tell Governance whether this sign-in actually established the context later searches depend on:
/// the scope Epic actually granted (vs. what was requested), whether a <c>patient</c> claim came back (the thing a
/// Patient/EHR-launch flow needs for unscoped <c>Patient</c> searches to succeed), and the token-cache key this
/// session was saved under (so a later lookup-time key can be compared against it without ever printing the
/// underlying CallerId/session identifier).
/// </summary>
public sealed record SmartAuthorizationCodeExchangeResult(
    string AccessToken,
    string? GrantedScope,
    bool PatientContextGranted,
    string TokenCacheKeyHash);
