using System.Linq;
using FHIRBridge.Runtime.Domain.Enums;

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

/// <summary>One operator-facing read of a <c>$export</c> status URL (see
/// <see cref="Abstractions.Connectors.IFhirBulkExportClient.GetStatusAsync"/>). Preserves what
/// <see cref="BulkExportPollResult"/> deliberately drops — the <c>X-Progress</c> header and the manifest's
/// <c>transactionTime</c>/<c>request</c> — none of which the poller needs to decide what to do next, but all of
/// which an operator watching a long-running export does. Kept separate from <see cref="BulkExportPollResult"/> so
/// the poller's decision path is untouched by a presentation concern.
///
/// <para>Two genuinely different shapes, because the server returns two: while the job runs, Bulk Data servers
/// answer 202 with NO body at all (Epic included), so <see cref="Files"/> is empty and <see cref="Progress"/> —
/// the free-text <c>X-Progress</c> header, e.g. "Searched 0 of 2 patients" — is the only detail available. The
/// manifest, and with it every per-type entry, exists only once the job completes.</para></summary>
public sealed record BulkExportStatusSnapshot(
    BulkExportPollStatus Status,
    /// <summary>The <c>X-Progress</c> header verbatim. Null on a completed job and on any server that doesn't
    /// send it — it's an optional header in the Bulk Data spec, and only some vendors (Epic reliably) populate it.</summary>
    string? Progress = null,
    IReadOnlyList<BulkExportFile>? Files = null,
    IReadOnlyList<BulkExportFile>? ErrorFiles = null,
    /// <summary>The manifest's <c>transactionTime</c> — the instant the server's data is current as of. Completed
    /// jobs only.</summary>
    DateTimeOffset? TransactionTime = null,
    /// <summary>The manifest's <c>request</c> — the original <c>$export</c> kick-off URL, echoed back by the
    /// server. Completed jobs only.</summary>
    string? Request = null,
    bool? RequiresAccessToken = null,
    TimeSpan? RetryAfter = null,
    string? ErrorMessage = null);

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

    /// <summary>
    /// Whether this source's server implements ONLY the Group-level <c>$export</c> operation, so a System- or
    /// Patient-scoped bulk export can never succeed against it no matter how the request is shaped. Epic states this
    /// outright — "Epic supports only the Group Export operation. We do not support _since or other bulk data
    /// operations at this time." (Epic's FHIR Bulk Data documentation, which also confirms Epic implements Bulk Data
    /// 1.0.1, where the kick-off is GET-only) — and athenahealth and eCW are documented Group-only too. A plain
    /// conformant FHIR server (<see cref="RuntimeSourceType.GenericFhir"/>, e.g. HAPI) does implement all three
    /// levels, so it is the one source type left un-narrowed. Kept as a narrow additive lookup rather than a switch
    /// on the vendor, same as <see cref="BulkExportGroupIds"/>, so it doesn't trip the architecture no-switch rule.
    /// </summary>
    public static bool IsGroupOnlyVendor(RuntimeSourceType sourceType) => sourceType != RuntimeSourceType.GenericFhir;

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

/// <summary>Extraction of the source vendor's own export-job id from a Bulk Data status URL, so the portal can show
/// it and an operator can look the job up on the vendor's side.</summary>
public static class BulkRequestIds
{
    /// <summary>
    /// The last non-empty path segment of a <c>$export</c> status URL — for Epic,
    /// <c>https://host/instance/api/FHIR/BulkRequest/0000000000176E6DC7DB51C0082DA988</c> yields
    /// <c>0000000000176E6DC7DB51C0082DA988</c>. Query string and any trailing slash are ignored.
    ///
    /// <para>Deliberately vendor-neutral last-segment parsing rather than an Epic-shaped pattern: every Bulk Data
    /// server puts the job's own identifier at the end of the status URL it hands back, so this yields a usable id
    /// for athenahealth and eCW too, and branching per vendor here would be the wrong shape (see the no-switch rule
    /// the architecture tests enforce).</para>
    ///
    /// <para>Returns null for anything unparseable, which is not an error condition: the id is a convenience for
    /// operators, and <c>BulkExportJob.StatusUrl</c> remains the job's actual identity for polling and resume.</para>
    /// </summary>
    public static string? FromStatusUrl(string? statusUrl)
    {
        if (string.IsNullOrWhiteSpace(statusUrl))
        {
            return null;
        }

        // Absolute is what a conformant server returns in Content-Location; the relative fallback keeps a
        // non-conformant (or test/stub) server from silently yielding nothing.
        var path = Uri.TryCreate(statusUrl, UriKind.Absolute, out var absolute)
            ? absolute.AbsolutePath
            : statusUrl.Split('?', '#')[0];

        var lastSegment = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(lastSegment) ? null : lastSegment;
    }
}

/// <summary>Vendor-specific reshaping of a Group <c>$export</c> GroupId — kept as a narrow, additive lookup (not a
/// switch on <c>ApplicationType</c>, so it doesn't trip the architecture no-switch rule) rather than a strategy class,
/// since today it's exactly one vendor's one quirk. athenahealth's Group-level bulk export targets a whole Practice,
/// addressed as <c>a-1.C-{Practice}</c> (see athenahealth's FHIR Bulk Export guide) — not a normal FHIR Group
/// resource id. Every other vendor's GroupId passes through completely unchanged.</summary>
public static class BulkExportGroupIds
{
    /// <summary>
    /// Resolves the GroupId to actually send for a Group-scoped export. For athenahealth: a bare numeric value
    /// (the practice number, whether typed directly into the wizard's Group ID field or falling back to the
    /// connection's own PracticeId when Group ID was left blank) is reformatted to <c>a-1.C-{Practice}</c>; a value
    /// that isn't purely numeric (already in athenahealth's expected form, or hand-authored) is left untouched. Every
    /// non-athenahealth source type returns <paramref name="groupId"/> verbatim — unchanged behavior for Epic and
    /// every other vendor.
    /// </summary>
    public static string? ResolveAthenahealthGroupId(RuntimeSourceType sourceType, string? groupId, string? practiceId)
    {
        if (sourceType != RuntimeSourceType.Athenahealth)
        {
            return groupId;
        }

        var candidate = string.IsNullOrWhiteSpace(groupId) ? practiceId : groupId;
        return candidate is { Length: > 0 } && candidate.All(char.IsDigit)
            ? $"a-1.C-{candidate}"
            : groupId;
    }
}
