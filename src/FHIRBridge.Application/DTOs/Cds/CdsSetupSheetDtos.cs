using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.DTOs.Cds;

// The Epic setup sheet (GET api/v1/cds/services/{id}/setup-sheet): every value the health system's Epic team
// must configure by hand for one service, since Epic does not read discovery. Configuration only — no PHI.

/// <param name="ServiceUrl">The CDS service endpoint to configure in Epic ({base}/cds-services/{serviceId}).</param>
/// <param name="FeedbackUrl">{ServiceUrl}/feedback.</param>
/// <param name="DefaultAudience">The JWT aud Segue expects when an issuer has no audience override (= ServiceUrl).</param>
/// <param name="PublicBaseUrlConfigured">False when Cds:PublicBaseUrl is unset: the URLs fall back to the admin's origin and live calls are refused.</param>
/// <param name="Prefetch">The prefetch the health system should configure (Epic does not read discovery).</param>
/// <param name="OverrideReasons">The dismiss reason codes to map to Epic override reasons.</param>
/// <param name="Fixes">The fixes this service may offer and the codes / preference-list items they use (empty when fixes are off).</param>
/// <param name="Links">Card links; smart links must be on the health system's SMART app allow-list.</param>
/// <param name="Issuers">The trusted issuers allowed to call this service, with the values each must sign with.</param>
/// <param name="Warnings">Short notes for the admin, e.g. "Cds:PublicBaseUrl is not set", "No trusted issuer is allowed".</param>
/// <param name="IsClinicallySignedOff">True while a clinical sign-off is on record for this service's current content.</param>
/// <param name="SignedOffBy">The reviewing clinician as recorded; null when not reviewed.</param>
/// <param name="SignedOffAtUtc">When the sign-off was recorded; null when not reviewed.</param>
/// <param name="SignOffNote">Short note on the review; null when none.</param>
public sealed record CdsSetupSheetDto(
    Guid ServiceInstanceId,
    string ServiceId,
    string Title,
    string Hook,
    bool IsEnabled,
    string ServiceUrl,
    string FeedbackUrl,
    string DefaultAudience,
    bool PublicBaseUrlConfigured,
    bool FetchMissingData,
    bool FixesEnabled,
    IReadOnlyList<CdsSetupSheetPrefetchDto> Prefetch,
    IReadOnlyList<CdsOverrideReasonOption> OverrideReasons,
    string OverrideReasonSystem,
    IReadOnlyList<CdsSetupSheetFixDto> Fixes,
    IReadOnlyList<CdsCardLinkOption> Links,
    IReadOnlyList<CdsSetupSheetIssuerDto> Issuers,
    IReadOnlyList<string> Warnings,
    DateTime GeneratedOnUtc,
    bool IsClinicallySignedOff = false,
    string? SignedOffBy = null,
    DateTime? SignedOffAtUtc = null,
    string? SignOffNote = null,
    CdsSetupSheetCrdDto? Crd = null);

/// <summary>The coverage requirements (Da Vinci CRD) part of a setup sheet; null for other services.</summary>
/// <param name="Hook">The hook to configure in Epic's "CRD Request" outgoing call for this service.</param>
/// <param name="EpicApis">The Epic APIs the CDS client registration needs for this service.</param>
/// <param name="UseSystemActions">True when orders are annotated through systemActions (needs ServiceRequest.Update (Unsigned Order)).</param>
/// <param name="DtrAppUrl">The documentation (DTR) app the cards link to; null when none (then nothing for the SMART allow-list).</param>
/// <param name="DtrLinkType">"smart" or "absolute".</param>
/// <param name="SegueJwksUrl">Segue's public JWKS (the jku of relayed calls); payers that receive relayed calls register it.</param>
/// <param name="Notes">Plain-language steps for the Epic team.</param>
public sealed record CdsSetupSheetCrdDto(
    string Hook,
    IReadOnlyList<string> EpicApis,
    bool UseSystemActions,
    string? DtrAppUrl,
    string? DtrLinkType,
    string SegueJwksUrl,
    IReadOnlyList<CdsSetupSheetPayerDto> Payers,
    IReadOnlyList<string> Notes);

/// <summary>A payer this service answers for, as the setup sheet shows it.</summary>
public sealed record CdsSetupSheetPayerDto(
    Guid Id,
    string Name,
    bool IsEnabled,
    IReadOnlyList<string> Identifiers,
    IReadOnlyList<string> PlanValues,
    int EnabledRuleCount,
    bool RelayEnabled,
    string? RelayServiceUrl,
    bool ForwardFhirAuthorization);

/// <param name="Key">The prefetch key Epic must send.</param>
/// <param name="Query">The FHIR query template, e.g. "AllergyIntolerance?patient={{context.patientId}}".</param>
/// <param name="FetchedIfMissing">True when Segue fetches the key itself if Epic does not send it (callback on and a fallback query exists).</param>
public sealed record CdsSetupSheetPrefetchDto(string Key, string Query, bool FetchedIfMissing);

/// <summary>One code a switched-on fix uses (one row per mapping; a delete fix has one row with no code).</summary>
/// <param name="Trigger">The mapping's trigger value as stored (e.g. "penicillins"); null for "any".</param>
/// <param name="TriggerLabel">The trigger's label from the handler's descriptor (as the portal's mapping table shows it).</param>
public sealed record CdsSetupSheetFixDto(
    string FixTypeKey,
    string Label,
    string ActionType,
    string ResourceType,
    string? Trigger,
    string? System,
    string? Code,
    string? Display,
    bool IsRecommended,
    string? TriggerLabel = null);

/// <param name="ExpectedAudience">The aud this issuer must sign with: its override, else the service URL.</param>
/// <param name="EffectiveFhirBaseUrl">The fhirServer Segue will call back to (FhirBaseUrl, else Issuer).</param>
public sealed record CdsSetupSheetIssuerDto(
    Guid Id,
    string Name,
    string Vendor,
    string Issuer,
    string? JwksUrl,
    bool HasPastedJwks,
    string? ExpectedSubject,
    string ExpectedAudience,
    string EffectiveFhirBaseUrl,
    bool AllowCallback,
    bool IsEnabled);

/// <summary>The result of POST api/v1/cds/trusted-issuers/{id}/test-callback (GET {fhirBase}/metadata, no token).</summary>
/// <param name="FhirBaseUrl">The URL tested (the issuer's EffectiveFhirBaseUrl).</param>
/// <param name="Reachable">True when /metadata answered 2xx with a CapabilityStatement.</param>
/// <param name="Message">One sentence for the admin.</param>
public sealed record CdsCallbackTestResultDto(
    Guid TrustedIssuerId,
    string FhirBaseUrl,
    bool AllowCallback,
    bool Reachable,
    int? HttpStatus,
    string? FhirVersion,
    string? Software,
    long DurationMs,
    string Message);
