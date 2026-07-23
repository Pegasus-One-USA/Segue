using System.Text.Json.Serialization;

namespace FHIRBridge.Application.DTOs;

public sealed record StartConfiguredPipelineRunRequest(
    IReadOnlyCollection<string>? ResourceTypes,
    string? TriggeredBy,
    string? CorrelationId)
{
    public bool RunDueSchedulesOnly { get; init; }
    public DateTime? ScheduledAtUtc { get; init; }

    /// <summary>
    /// When set, only these specific routes run and the cron schedule re-check is bypassed — the scheduler has
    /// already decided which routes are due (catch-up aware), so the run executes exactly those, even if the
    /// current minute no longer matches the cron. Null = the legacy resource-type + RunDueSchedulesOnly behavior.
    /// </summary>
    public IReadOnlyCollection<Guid>? RouteIds { get; init; }

    /// <summary>
    /// When true, source resources are pulled via a FHIR Bulk Data <c>$export</c> job (kick off → poll → NDJSON)
    /// instead of a paged <c>$search</c>. The rest of the pipeline (govern → map → write) is unchanged, so the run
    /// still appears in Runs. Requires a source whose server supports bulk export.
    /// </summary>
    public bool UseBulkExport { get; init; }

    /// <summary>
    /// Server-set only (never client-supplied — see <see cref="JsonIgnoreAttribute"/>). True only for the
    /// synchronous, non-bulk-export branch of <c>PipelineRunsController.Start</c>, which is the one call path that
    /// actually has an HTTP response to carry a Download-mode destination's bytes back through.
    /// </summary>
    [JsonIgnore]
    public bool AllowInlineDownload { get; init; }
}
