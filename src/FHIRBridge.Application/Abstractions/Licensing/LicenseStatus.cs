namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>
/// Lifecycle state of the currently-resolved license. This stage only ever reports one of these values —
/// nothing in this stage blocks or gates behavior on it. See <see cref="LicenseStatus"/>.
/// </summary>
public enum LicenseState
{
    /// <summary>No license token could be resolved from any configured source.</summary>
    Unlicensed,

    /// <summary>A signature-valid, currently-in-effect license.</summary>
    Active,

    /// <summary>Reserved for a future short window after expiry where the product still runs unblocked
    /// but nags the admin to renew. Not produced by <c>SignedLicenseValidator</c> in this stage — every
    /// expired token currently maps to <see cref="Expired"/> instead. Kept in the enum now so the second
    /// (enforcement) stage can introduce grace-period behavior without another public-shape change.</summary>
    Grace,

    /// <summary>A signature-valid license whose <c>exp</c> claim is in the past. Claims are still parsed
    /// and reported (<see cref="LicenseStatus.CustomerName"/>, <see cref="LicenseStatus.Limits"/>, etc.).</summary>
    Expired,

    /// <summary>The token was malformed, had a bad/missing signature, failed <c>nbf</c>, or was issued by
    /// an unrecognized issuer. See <see cref="LicenseStatus.InvalidReason"/> for a short diagnostic.</summary>
    Invalid
}

/// <summary>
/// One hospital/organization a license permits connecting to, identified by vendor + FHIR base URL — the
/// same two axes a <c>SourceConnection</c> already carries (see <c>SourceConnection.BaseUrl</c>). This
/// stage never matches a <c>SourceConnection</c> against these; it exists purely so a license's terms can
/// name specific allowed hospitals, for a future enforcement stage to consume unchanged.
/// </summary>
public sealed record AllowedHospital(string Vendor, string BaseUrl, string? DisplayName);

/// <summary>
/// The independently-tracked quota/restriction dimensions a license may cap. Every numeric field uses
/// <see cref="Unlimited"/> (<c>-1</c>) to mean unrestricted for that dimension, rather than a nullable
/// int — a license minted with a blank/omitted numeric claim is parsed as <see cref="Unlimited"/> too, so
/// both spellings mean the same thing. <c>null</c>/empty on a list-shaped field means unrestricted for that
/// dimension. This stage never reads these values for enforcement; they exist purely so a license's terms
/// can be reported, and so a second (quota-enforcement) stage can consume exactly these names without
/// another public-shape change.
/// </summary>
public sealed record LicenseLimits(
    int MaxUsers = LicenseLimits.Unlimited,
    int MaxWorkflows = LicenseLimits.Unlimited,
    int MaxSourceConnections = LicenseLimits.Unlimited,
    /// <summary>Source vendor type names (<c>SourceSystemType</c> member names, e.g. "Epic", "Healow") this
    /// license permits configuring. <c>null</c>/empty means every source type is allowed.</summary>
    IReadOnlyList<string>? AllowedSourceTypes = null,
    /// <summary>Specific hospital endpoints (vendor + FHIR base URL) this license permits connecting to.
    /// <c>null</c>/empty means any hospital is allowed (no per-hospital-count cap exists — hospitals are
    /// gated only by this allow-list, not by a number).</summary>
    IReadOnlyList<AllowedHospital>? AllowedHospitals = null,
    /// <summary>No live "processed this month" counter exists yet — this is reported as a bare limit only;
    /// counting actual processed records is a future enforcement stage.</summary>
    int MaxProcessedRecordsPerMonth = LicenseLimits.Unlimited,
    /// <summary>FHIR resource type names (<c>SupportedFhirResourceTypes.All</c> members, e.g. "Patient",
    /// "Observation") this license permits processing. <c>null</c>/empty means every resource type is
    /// allowed — same convention as <see cref="AllowedSourceTypes"/>.</summary>
    IReadOnlyList<string>? AllowedResourceTypes = null,
    /// <summary>Destination type names (<c>DestinationType</c> member names, e.g. "SqlServer", "Sftp") this
    /// license permits writing to. <c>null</c>/empty means every destination type is allowed — same
    /// convention as <see cref="AllowedSourceTypes"/>.</summary>
    IReadOnlyList<string>? AllowedDestinationTypes = null,
    /// <summary>Hard cap on successful pipeline/workflow executions (both planes combined, matching
    /// <c>LicenseUsageCounts.WorkflowCount</c>'s own combined-plane convention) within the current calendar
    /// month. Enforced the same way as <see cref="MaxProcessedRecordsPerMonth"/>: checked at run-TRIGGER time
    /// (<c>ILicenseQuotaGuard.EnsureCanStartNewRunAsync</c>), short-TTL cached, never mid-run.</summary>
    int MaxSuccessfulWorkflowExecutionsPerMonth = LicenseLimits.Unlimited)
{
    /// <summary>Sentinel value for every numeric field on this record meaning "no cap for this dimension."</summary>
    public const int Unlimited = -1;
}

/// <summary>
/// Immutable, parsed-and-verified snapshot of the current license, as produced by
/// <c>SignedLicenseValidator</c> and held by <see cref="ILicenseService.Current"/>. This stage is
/// verification/reporting only: nothing in the product blocks or gates on any of these values yet.
/// </summary>
public sealed record LicenseStatus(
    LicenseState State,
    string? CustomerName,
    string? Edition,
    DateTime? IssuedUtc,
    DateTime? ExpiresUtc,
    LicenseLimits? Limits,
    IReadOnlyList<string> Features,
    string? InvalidReason)
{
    /// <summary>True for every state except <see cref="LicenseState.Unlicensed"/> — i.e. some token was
    /// found and parsed, even if it turned out to be expired or invalid.</summary>
    public bool IsPresent => State is not LicenseState.Unlicensed;

    public bool IsExpired => State == LicenseState.Expired;

    /// <summary>Whole days until <see cref="ExpiresUtc"/>, floored at zero; <c>null</c> when the license
    /// carries no expiry (or none was ever resolved).</summary>
    public int? DaysRemaining => ExpiresUtc.HasValue
        ? Math.Max(0, (int)(ExpiresUtc.Value - DateTime.UtcNow).TotalDays)
        : null;

    public static LicenseStatus Unlicensed { get; } =
        new(LicenseState.Unlicensed, null, null, null, null, null, Array.Empty<string>(), null);
}
