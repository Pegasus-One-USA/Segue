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
    LicenseUsageDto? Usage);

/// <summary>Body of <c>POST /api/v1/license</c>.</summary>
public sealed record ApplyLicenseRequest(string Token);
