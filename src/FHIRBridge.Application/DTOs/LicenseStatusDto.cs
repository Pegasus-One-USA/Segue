namespace FHIRBridge.Application.DTOs;

/// <summary>Wire shape for one license-allowed hospital. Mirrors
/// <c>FHIRBridge.Application.Abstractions.Licensing.AllowedHospital</c> field for field.</summary>
public sealed record AllowedHospitalDto(string Vendor, string BaseUrl, string? DisplayName);

/// <summary>Wire shape for the license's quota/restriction dimensions. Every numeric field uses <c>-1</c> to
/// mean unlimited/unrestricted for that dimension (see <c>LicenseLimits.Unlimited</c>); <c>null</c>/empty on
/// a list-shaped field means unrestricted for that dimension. Mirrors
/// <c>FHIRBridge.Application.Abstractions.Licensing.LicenseLimits</c> field-for-field so the controller
/// never leaks that Abstractions record directly.</summary>
public sealed record LicenseLimitsDto(
    int MaxUsers,
    int MaxWorkflows,
    int MaxSourceConnections,
    IReadOnlyList<string>? AllowedSourceTypes,
    IReadOnlyList<AllowedHospitalDto>? AllowedHospitals,
    int MaxProcessedRecordsPerMonth,
    IReadOnlyList<string>? AllowedResourceTypes,
    IReadOnlyList<string>? AllowedDestinationTypes,
    int MaxSuccessfulWorkflowExecutionsPerMonth);

/// <summary>Live usage counts mirroring <c>FHIRBridge.Application.Abstractions.Licensing.LicenseUsageCounts</c>,
/// for the portal to display alongside each <see cref="LicenseLimitsDto"/> dimension (e.g. "Users: 2 / 5").
/// <see cref="SuccessfulExecutionsThisMonth"/> instead mirrors
/// <c>LicenseUsageExecutionStats.SuccessfulExecutionsThisMonth</c> — the current-calendar-month count that
/// <see cref="LicenseLimitsDto.MaxSuccessfulWorkflowExecutionsPerMonth"/> caps, shown here alongside the other
/// live counts instead of as a bare limit under "Additional Restrictions".</summary>
public sealed record LicenseUsageDto(
    int UserCount,
    int SourceConnectionCount,
    int TenantCount,
    int WorkflowCount,
    long SuccessfulExecutionsThisMonth);

/// <summary>Wire shape returned by <c>LicenseController.Get</c> — the controller-facing projection of
/// <c>FHIRBridge.Application.Abstractions.Licensing.LicenseStatus</c>.</summary>
public sealed record LicenseStatusDto(
    string State,
    string? CustomerName,
    string? Edition,
    DateTime? IssuedUtc,
    DateTime? ExpiresUtc,
    LicenseLimitsDto? Limits,
    IReadOnlyList<string> Features,
    string? InvalidReason,
    bool IsPresent,
    bool IsExpired,
    int? DaysRemaining,
    LicenseUsageDto? Usage,
    bool AlreadyActive = false,
    /// <summary>Mirrors <c>License:AllowTestingUtilities</c> (off by default) — whether this install's
    /// "Clear License"/"Clear License History" testing/support endpoints are callable at all right now.
    /// The portal uses this to decide whether to render that card; the endpoints themselves also check
    /// the same setting server-side, so hiding the button is a UX nicety here, not the actual gate.</summary>
    bool AllowTestingUtilities = false);

/// <summary>Body of <c>POST /api/v1/license</c>.</summary>
public sealed record ApplyLicenseRequest(string Token);

/// <summary>Wire shape for one row returned by <c>GET /api/v1/license/history</c> — mirrors
/// <c>FHIRBridge.Domain.Entities.Licensing.LicenseHistoryEntry</c>, plus every quota/restriction dimension
/// re-parsed from that entry's own stored token (never the raw token itself — no UI need to expose that),
/// so the portal can show a full detail view for any past license, not just the currently-active one.</summary>
public sealed record LicenseHistoryEntryDto(
    Guid Id,
    DateTime AppliedUtc,
    string? CustomerName,
    string? Edition,
    string State,
    DateTime? ExpiresUtc,
    bool IsCurrent,
    /// <summary>When this token was minted (its <c>nbf</c> claim) — distinct from <see cref="AppliedUtc"/>
    /// (when THIS install activated it), which can be well after issuance.</summary>
    DateTime? IssuedUtc,
    LicenseLimitsDto? Limits,
    IReadOnlyList<string> Features,
    /// <summary>The <c>requestKey</c> claim, when this license was minted against a specific License
    /// Request — null if it wasn't.</summary>
    string? RequestKey);
