namespace FHIRBridge.LicenseServer.Licensing;

/// <summary>
/// One entry of the license's <c>allowedHospitals</c> claim: <c>{ vendor, baseUrl, displayName }</c>.
/// Free-form here (vendor + base URL typed by the admin) since this standalone project has no access to
/// the main repo's <c>EhrEndpoint</c> directory.
/// </summary>
public sealed record AllowedHospital(string Vendor, string BaseUrl, string? DisplayName);

/// <summary>
/// Every field a FHIRBridge license can carry, independent of how it's collected (form post) or where it's
/// going (the signed JWS claims, or an <see cref="Domain.IssuedLicense"/> audit row). Mirrors exactly the
/// claim shape <c>SignedLicenseValidator</c> in the main FHIRBridge repo expects.
/// </summary>
public sealed class LicenseFields
{
    /// <summary>Sentinel meaning "unlimited" for every numeric field below — mirrors the main repo's
    /// <c>LicenseLimits.Unlimited</c>.</summary>
    public const int Unlimited = -1;

    public required string CustomerId { get; set; }

    public required string CustomerName { get; set; }

    public required string Edition { get; set; }

    public DateTime ExpiresUtc { get; set; }

    public int MaxUsers { get; set; } = Unlimited;

    public int MaxWorkflows { get; set; } = Unlimited;

    public int MaxSourceConnections { get; set; } = Unlimited;

    public int MaxProcessedRecordsPerMonth { get; set; } = Unlimited;

    /// <summary>Null/empty means "all source types allowed" — same convention as the main repo's minter.</summary>
    public IReadOnlyList<string>? AllowedSourceTypes { get; set; }

    /// <summary>Null/empty means "any hospital allowed".</summary>
    public IReadOnlyList<AllowedHospital>? AllowedHospitals { get; set; }

    public IReadOnlyList<string> Features { get; set; } = Array.Empty<string>();

    /// <summary>Null/empty means "all FHIR resource types allowed" — same convention as
    /// <see cref="AllowedSourceTypes"/>, mirroring the main repo's <c>LicenseLimits.AllowedResourceTypes</c>.</summary>
    public IReadOnlyList<string>? AllowedResourceTypes { get; set; }

    /// <summary>Null/empty means "all destination types allowed" — same convention as
    /// <see cref="AllowedSourceTypes"/>, mirroring the main repo's <c>LicenseLimits.AllowedDestinationTypes</c>.</summary>
    public IReadOnlyList<string>? AllowedDestinationTypes { get; set; }

    /// <summary>Hard cap on successful pipeline/workflow executions (both planes combined) per calendar
    /// month, mirroring the main repo's <c>LicenseLimits.MaxSuccessfulWorkflowExecutionsPerMonth</c>.</summary>
    public int MaxSuccessfulWorkflowExecutionsPerMonth { get; set; } = Unlimited;

    /// <summary>Minutes after minting this license must be applied (POST /api/v1/license) within, or the
    /// main repo's SignedLicenseValidator rejects it as expired. Null means no activation deadline — the
    /// customer can apply it whenever they like, same as every license minted before this field existed.
    /// Never affects an already-applied, currently-running license (see SignedLicenseValidator.Validate's
    /// remarks in the main repo for the full reasoning).</summary>
    public int? ActivationWindowMinutes { get; set; }

    /// <summary>The requesting install's own <c>LicenseRequest.UniqueKey</c> (see <c>Domain.LicenseRequest</c>),
    /// embedded as the token's <c>requestKey</c> claim so that install's own <c>SignedLicenseValidator</c>
    /// refuses to apply this license unless it matches the request it made. Null when minted with no
    /// associated request (e.g. a hand-typed CustomerId, or a license predating this feature) — the main
    /// repo only enforces the check when a request exists locally, so an absent claim is always accepted.</summary>
    public string? RequestKey { get; set; }
}
