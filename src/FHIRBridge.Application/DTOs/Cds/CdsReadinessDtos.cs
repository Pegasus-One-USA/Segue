namespace FHIRBridge.Application.DTOs.Cds;

// The go-live readiness report (GET api/v1/cds/readiness): what must be in place before a health system's EHR
// (Epic first) calls Segue for real. Configuration only — never secrets (no keys, tokens or connection strings)
// and never patient data.

/// <summary>The status values a readiness check (and the report as a whole) can have.</summary>
public static class CdsReadinessStatus
{
    /// <summary>Ready.</summary>
    public const string Ok = "ok";

    /// <summary>Works, but should be fixed or consciously accepted before go-live.</summary>
    public const string Warning = "warning";

    /// <summary>Live CDS calls will fail (or are not safe) until this is fixed.</summary>
    public const string Blocker = "blocker";
}

/// <summary>Stable keys of the readiness checks (the portal and tests rely on them; never rename one).</summary>
public static class CdsReadinessCheckKeys
{
    public const string PublicBaseUrl = "public-base-url";
    public const string ReplayProtection = "replay-protection";
    public const string TrustedIssuer = "trusted-issuer";
    public const string IssuerJwksUrl = "issuer-jwks-url";
    public const string IssuerSubject = "issuer-subject";
    public const string IssuerFhirBaseUrl = "issuer-fhir-base-url";
    public const string EnabledService = "enabled-service";
    public const string FixesSignOff = "fixes-sign-off";
    public const string SampleDataSignOff = "sample-data-sign-off";
    public const string License = "license";
    public const string RateLimit = "rate-limit";

    // Coverage requirements (Da Vinci CRD); present only when a coverage requirements service or a payer exists.
    public const string CrdPayerCoverage = "crd-payer-coverage";
    public const string CrdRelayHttps = "crd-relay-https";
    public const string CrdRulesExpiring = "crd-rules-expiring";
    public const string CrdSignOff = "crd-sign-off";
}

/// <summary>One readiness check.</summary>
/// <param name="Key">Stable key from <see cref="CdsReadinessCheckKeys"/>.</param>
/// <param name="Label">Short name for the admin.</param>
/// <param name="Status">ok | warning | blocker (<see cref="CdsReadinessStatus"/>).</param>
/// <param name="Detail">What was found, in one or two sentences. Names services and issuers, never secrets.</param>
/// <param name="HowToFix">What to do about it; null when the check is ok.</param>
public sealed record CdsReadinessCheckDto(string Key, string Label, string Status, string Detail, string? HowToFix);

/// <param name="Status">The worst status of all checks.</param>
public sealed record CdsReadinessDto(string Status, IReadOnlyList<CdsReadinessCheckDto> Checks, DateTime GeneratedOnUtc);
