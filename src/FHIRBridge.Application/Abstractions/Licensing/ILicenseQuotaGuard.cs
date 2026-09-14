using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>
/// Real enforcement layer on top of <see cref="ILicenseService"/>/<see cref="ILicenseUsageCountsProvider"/>/
/// <see cref="ILicenseUsageExecutionStatsProvider"/>: every method here either returns normally (the create/run
/// may proceed) or throws (see <c>FHIRBridge.SharedKernel.Exceptions.LicenseQuotaExceededException"</c> /
/// <c>LicenseRestrictionViolationException</c>) to block it.
///
/// This is a commercial boundary, not a security boundary — every implementation MUST fail open: no license
/// present, an unlimited dimension, an unrestricted (null/empty) allow-list, or the guard's own dependency
/// throwing must all result in the call passing, never a customer being locked out of their own console by an
/// internal bug here.
///
/// Four explicit methods per quota dimension (plus two allow-list-only checks and one run-trigger check),
/// deliberately NOT a registry/switch over a "quota dimension" enum — that convention exists for
/// vendor-extensible axes (see <c>ApplicationType</c>/<c>FhirSourceConnectorBase</c>); these dimensions are
/// fixed by <see cref="LicenseLimits"/>'s own shape and do not grow the way vendor connectors do.
/// </summary>
public interface ILicenseQuotaGuard
{
    /// <summary>Throws when the live user count is already at/over <see cref="LicenseLimits.MaxUsers"/>. Call
    /// before persisting a brand-new <c>User</c> row (local create or invite) — never on update/resend paths.</summary>
    Task EnsureUserQuotaAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Throws when the live source-connection count is already at/over
    /// <see cref="LicenseLimits.MaxSourceConnections"/>, OR when <see cref="LicenseLimits.AllowedSourceTypes"/>
    /// is set and <paramref name="vendorType"/> isn't on it, OR when <see cref="LicenseLimits.AllowedHospitals"/>
    /// is set and no entry matches both <paramref name="vendorType"/> (case-insensitively) and
    /// <paramref name="baseUrl"/> (exact match). Call before persisting a brand-new <c>SourceConnection</c> row.</summary>
    Task EnsureSourceConnectionQuotaAvailableAsync(
        SourceSystemType vendorType, string baseUrl, CancellationToken cancellationToken);

    /// <summary>Allow-list check only (no count, unlike <see cref="EnsureSourceConnectionQuotaAvailableAsync"/>):
    /// re-validates <paramref name="vendorType"/>/<paramref name="baseUrl"/> against
    /// <see cref="LicenseLimits.AllowedSourceTypes"/>/<see cref="LicenseLimits.AllowedHospitals"/> at the moment
    /// an EXISTING source connection is actually used — call once per distinct source connection a pipeline/
    /// workflow run is about to touch, at that run's TRIGGER point, alongside <see cref="EnsureCanStartNewRunAsync"/>.
    /// This is what catches a hospital/vendor being dropped from the license on renewal, or a connection's
    /// BaseUrl being edited after creation — neither of which <c>LicenseEnforcementSaveChangesInterceptor</c>
    /// (create-time only) would ever see.</summary>
    Task EnsureSourceConnectionStillAllowedAsync(
        SourceSystemType vendorType, string baseUrl, CancellationToken cancellationToken);

    /// <summary>Throws when the live workflow count (Configured-Pipeline routes + Runtime-plane workflow
    /// definitions combined — exactly <see cref="LicenseUsageCounts.WorkflowCount"/>) is already at/over
    /// <see cref="LicenseLimits.MaxWorkflows"/>. Call before persisting a brand-new <c>ResourcePipelineRoute"</c>
    /// or <c>WorkflowDefinition</c> row — never on an edit of an existing one.</summary>
    Task EnsureWorkflowQuotaAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Allow-list check only (no count): throws when <see cref="LicenseLimits.AllowedResourceTypes"/>
    /// is set and <paramref name="resourceType"/> isn't on it. Call at <c>ResourcePipelineRoute</c> creation.</summary>
    Task EnsureResourceTypeAllowedAsync(string resourceType, CancellationToken cancellationToken);

    /// <summary>Allow-list check only (no count): throws when <see cref="LicenseLimits.AllowedDestinationTypes"/>
    /// is set and <paramref name="destinationType"/>'s name isn't on it. Call at <c>DestinationConfiguration</c>
    /// creation.</summary>
    Task EnsureDestinationTypeAllowedAsync(DestinationType destinationType, CancellationToken cancellationToken);

    /// <summary>Called at every pipeline-run TRIGGER point (both the Configured Pipeline and Runtime planes),
    /// never mid-run: throws when the license is truly expired (<see cref="LicenseStatus.IsExpired"/>), or when
    /// <see cref="LicenseLimits.MaxProcessedRecordsPerMonth"/> is set and this calendar month's processed-record
    /// count is already at/over it. An in-flight run is never interrupted by this check — it only ever gates a
    /// NEW run from starting.</summary>
    Task EnsureCanStartNewRunAsync(CancellationToken cancellationToken);
}
