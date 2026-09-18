namespace FHIRBridge.Api.Workflows;

/// <summary>
/// One row of the (Runtime Plane) Execution History screen — a workflow run joined with its workflow's name and
/// first source connection, shaped to match the Configured Pipeline's <c>PipelineRunRouteExecutionDto</c> so the
/// portal can render both with the same table.
/// </summary>
public sealed record WorkflowRunHistoryDto(
    Guid Id,
    Guid WorkflowDefinitionId,
    string PipelineName,
    string? SourceName,
    string? SourceSystemType,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? TriggeredBy,
    string? TriggerType,
    int NodeRunCount,
    string? ErrorMessage,
    int WorkflowDefinitionVersion,
    string? CorrelationId,
    string? ErrorReferenceId,
    string? BulkRequestId)
{
    public long? DurationMs => CompletedAt.HasValue
        ? (long)(CompletedAt.Value - StartedAt).TotalMilliseconds
        : null;
}

/// <summary>
/// A page of Execution History rows plus the distinct source systems that actually appear in the history at all.
/// Same shape and reasoning as <c>WorkflowSummaryPageDto</c>: the Source filter offers only vendors with real runs
/// behind them (instead of every EHR the platform can talk to), and the option list is computed over the UNFILTERED
/// set so unchecking every box doesn't empty the list it was chosen from.
/// </summary>
public sealed record WorkflowRunHistoryPageDto(
    IReadOnlyList<WorkflowRunHistoryDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<string> AvailableSourceSystemTypes);

/// <summary>
/// A live read of one run's FHIR Bulk Data <c>$export</c> job, proxied from the source server on demand — backs the
/// Execution History row's "Bulk Data Status Request" popup.
///
/// <para>Renders two genuinely different states off ONE list. While the job runs, the server answers 202 with no
/// body, so <see cref="ResourceTypes"/> is built from what the run REQUESTED (every entry <c>Pending</c>, no file
/// count) and <see cref="Progress"/> — the server's free-text <c>X-Progress</c> — is the only live detail. Once it
/// completes, the same list is rebuilt from the manifest with real file counts. One shape, so the UI binds a single
/// table either way.</para>
///
/// <para>Deliberately carries NO output URLs. The manifest's <c>output[].url</c> entries are signed, directly
/// downloadable NDJSON of bulk PHI; they are grouped into counts server-side so they never reach a browser.</para>
/// </summary>
public sealed record BulkExportStatusDto(
    string? BulkRequestId,
    /// <summary>InProgress, Completed or Failed — the live read, not <c>BulkExportJob.Status</c>.</summary>
    string Status,
    /// <summary>The <c>X-Progress</c> header verbatim, e.g. "Searched 0 of 2 patients". Null on servers that
    /// don't send it (it is optional in the spec) and on a completed job.</summary>
    string? Progress,
    DateTime KickedOffOnUtc,
    DateTime? NextPollNotBeforeUtc,
    int PollAttemptCount,
    DateTimeOffset? TransactionTime,
    /// <summary>The kick-off URL the server echoes back in its manifest. Completed jobs only.</summary>
    string? Request,
    bool? RequiresAccessToken,
    IReadOnlyList<BulkExportResourceTypeStatusDto> ResourceTypes,
    /// <summary>Free-text issues from the manifest's <c>error</c> array — where a server reports resource types it
    /// tried and refused even though the job as a whole succeeded.</summary>
    IReadOnlyList<string> Errors,
    int? RetryAfterSeconds,
    string? ErrorMessage);

/// <summary>One resource type's state within an export. <paramref name="FileCount"/> is the number of NDJSON FILES
/// the server produced, never a record count — a single file may hold one resource or fifty thousand, and the real
/// total is knowable only after download. Null while the type is still <c>Pending</c>.</summary>
public sealed record BulkExportResourceTypeStatusDto(
    string ResourceType,
    int? FileCount,
    /// <summary><c>Pending</c> or <c>Ready</c>.</summary>
    string State);

/// <summary>All-time run count per <see cref="FHIRBridge.Runtime.Domain.Workflows.WorkflowRunStatus"/>, across
/// every workflow definition — backs the Dashboard's status stat tiles.</summary>
public sealed record WorkflowRunStatusCountsDto(
    int Pending,
    int Running,
    int Succeeded,
    int Failed,
    int Cancelled,
    int PartialSuccess);
