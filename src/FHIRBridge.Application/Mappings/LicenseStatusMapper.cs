using System.Linq;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Mappings;

public static class LicenseStatusMapper
{
    /// <summary>Maps the verified license snapshot to its wire shape. <paramref name="usageCounts"/> and
    /// <paramref name="executionStats"/> are populated into <see cref="LicenseStatusDto.Usage"/> only when a
    /// license is present (<see cref="LicenseStatus.IsPresent"/>) — there is nothing meaningful to show usage
    /// against for an <see cref="LicenseState.Unlicensed"/> status, so callers may pass <c>null</c> (or omit
    /// them) in that case without affecting the result.</summary>
    public static LicenseStatusDto ToDto(
        LicenseStatus status,
        LicenseUsageCounts? usageCounts = null,
        LicenseUsageExecutionStats? executionStats = null,
        bool alreadyActive = false,
        bool allowTestingUtilities = false) =>
        new(
            status.State.ToString(),
            status.CustomerName,
            status.Edition,
            status.IssuedUtc,
            status.ExpiresUtc,
            ToLimitsDto(status.Limits),
            status.Features,
            status.InvalidReason,
            status.IsPresent,
            status.IsExpired,
            status.DaysRemaining,
            status.IsPresent && usageCounts is { } counts
                ? new LicenseUsageDto(
                    counts.UserCount,
                    counts.SourceConnectionCount,
                    counts.TenantCount,
                    counts.WorkflowCount,
                    executionStats?.SuccessfulExecutionsThisMonth ?? 0)
                : null,
            alreadyActive,
            allowTestingUtilities);

    /// <summary>Shared by <see cref="ToDto"/> and <c>LicenseController.GetHistory</c> — every quota/allow-list
    /// dimension a license can carry, mapped to its wire shape.</summary>
    public static LicenseLimitsDto? ToLimitsDto(LicenseLimits? limits) =>
        limits is null
            ? null
            : new LicenseLimitsDto(
                limits.MaxUsers,
                limits.MaxWorkflows,
                limits.MaxSourceConnections,
                limits.AllowedSourceTypes,
                limits.AllowedHospitals?
                    .Select(h => new AllowedHospitalDto(h.Vendor, h.BaseUrl, h.DisplayName))
                    .ToArray(),
                limits.MaxProcessedRecordsPerMonth,
                limits.AllowedResourceTypes,
                limits.AllowedDestinationTypes,
                limits.MaxSuccessfulWorkflowExecutionsPerMonth);
}
