using System.Linq;

namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>The scope of a FHIR Bulk Data export ($export) kick-off.</summary>
public enum BulkExportScope
{
    /// <summary>System-level export: <c>[base]/$export</c>.</summary>
    System = 0,

    /// <summary>All patients: <c>[base]/Patient/$export</c>.</summary>
    Patient = 1,

    /// <summary>A group's members: <c>[base]/Group/{id}/$export</c>.</summary>
    Group = 2
}

/// <summary>Describes a FHIR Bulk Data <c>$export</c> request.</summary>
public sealed record FhirBulkExportRequest(
    BulkExportScope Scope = BulkExportScope.Patient,
    string? GroupId = null,
    IReadOnlyCollection<string>? ResourceTypes = null,
    DateTimeOffset? Since = null,
    string? TypeFilter = null,
    IReadOnlyCollection<string>? PatientIds = null,
    string? OutputFormat = null);

/// <summary>One NDJSON output file produced by a completed export.</summary>
public sealed record BulkExportFile(string ResourceType, string Url);

/// <summary>One entry from a completed export manifest's <c>error</c> array (FHIR Bulk Data spec) — an
/// OperationOutcome NDJSON file describing a resource type the server couldn't/wouldn't include in the export
/// even though the job as a whole succeeded (e.g. a type not supported or not authorized for this client's
/// registration). The manifest itself never names the affected resource type structurally — <see cref="Diagnostics"/>
/// is the server's free-text explanation, which in practice (e.g. Epic) names it.</summary>
public sealed record BulkExportPartialFailure(string? Severity, string? Code, string Diagnostics);

/// <summary>Outcome of a single <c>$export</c> status-URL poll (see
/// <see cref="Abstractions.Connectors.IFhirBulkExportClient.PollOnceAsync"/>) — deliberately returned rather than
/// thrown for <see cref="BulkExportPollStatus.Failed"/>, so a caller polling on a schedule (rather than blocking in
/// a loop) can persist the failure without an unhandled exception skipping that persistence.</summary>
public sealed record BulkExportPollResult(
    BulkExportPollStatus Status,
    IReadOnlyList<BulkExportFile>? Files = null,
    TimeSpan? RetryAfter = null,
    string? ErrorMessage = null,
    IReadOnlyList<BulkExportFile>? ErrorFiles = null);

/// <summary>Status of a single bulk-export poll attempt.</summary>
public enum BulkExportPollStatus
{
    /// <summary>Server returned 202 Accepted — job still running.</summary>
    InProgress,

    /// <summary>Server returned 200 OK with the completion manifest.</summary>
    Completed,

    /// <summary>Server returned an unexpected status.</summary>
    Failed
}

/// <summary>Shared parsing of the persisted export-scope token to <see cref="BulkExportScope"/> (System is the safe
/// default for unset/legacy/unknown values). Used by both pipeline planes so the mapping never diverges.</summary>
public static class BulkExportScopes
{
    public static BulkExportScope Parse(string? exportScope) => exportScope?.Trim().ToLowerInvariant() switch
    {
        "group" => BulkExportScope.Group,
        "patient" => BulkExportScope.Patient,
        _ => BulkExportScope.System,
    };

    /// <summary>Resolves the <c>_type</c> value to actually send for a Group <c>$export</c> kick-off. A job scoped to
    /// ONLY <c>Patient</c> (e.g. <c>Group/{id}/$export?_type=Patient</c>) trips a real Epic Interconnect Group-export
    /// limitation: to materialize the group's Patient records, Epic resolves membership via an internal, unscoped
    /// Patient search in that lone-type case, which its own business rule then rejects ("requires demographics or
    /// _id parameter", code 59159). Epic only exhibits this for a single, Patient-only <c>_type</c> on a GROUP
    /// export — a job requesting Patient alongside any other resource type is unaffected, and a System-level export
    /// (which reads directly from the tenant's Patient store with no membership-resolution step) doesn't need this
    /// workaround at all, so this deliberately only applies to Group scope — narrowing it to System too would trade
    /// a small, targeted Patient fetch for an unrestricted whole-tenant export with no evidence Epic needs it there.
    /// Omitting <c>_type</c> entirely (server default: every resource type it's willing to export for the group)
    /// sidesteps the lone-type code path; callers already filter the resulting NDJSON down to whichever resource
    /// types they actually route, so this only ever widens what's returned, never what's consumed.</summary>
    public static IReadOnlyCollection<string>? ResolveTypeParameter(BulkExportScope scope, IReadOnlyCollection<string>? resourceTypes)
    {
        if (scope != BulkExportScope.Group)
        {
            return resourceTypes;
        }

        return resourceTypes is { Count: 1 } && resourceTypes.Any(type => string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase))
            ? null
            : resourceTypes;
    }
}
