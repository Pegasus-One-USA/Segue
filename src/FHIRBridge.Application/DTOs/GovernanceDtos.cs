namespace FHIRBridge.Application.DTOs;

public sealed record AuditLogDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string Actor,
    string Module,
    string Action,
    string? EntityType,
    string? EntityId,
    string? EntityName,
    string? OldValueJson,
    string? NewValueJson,
    string Status,
    string? IpAddress,
    string? CorrelationId);

public sealed record DataAccessLogDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string Actor,
    string ResourceType,
    string? ResourceId,
    string Action,
    string? PatientId,
    string? Purpose,
    Guid? PipelineRunId,
    string? CorrelationId);

public sealed record AuthenticationLogDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string? UserEmail,
    string AuthenticationType,
    bool Success,
    string? FailureReason,
    string? IpAddress,
    string? CorrelationId);

public sealed record SecurityEventDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string Severity,
    string EventType,
    string? UserEmail,
    string? IpAddress,
    string? Details,
    bool Resolved,
    string? CorrelationId);

public sealed record AuthorizationLogDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string? UserEmail,
    string RequestPath,
    string PermissionCode,
    string Result,
    string? IpAddress,
    string? CorrelationId);

public sealed record SchedulerHistoryDto(
    Guid Id,
    string SchedulerId,
    DateTime RunTimeUtc,
    string Status,
    int RouteCount,
    string? CorrelationId);

public sealed record RetryHistoryDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string Context,
    int RetryNumber,
    int DelayMilliseconds,
    string Reason,
    string? CorrelationId);

public sealed record ErrorLogDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string Severity,
    string ExceptionType,
    string Message,
    string? StackTrace,
    string? Module,
    string? CorrelationId,
    string? ErrorReferenceId = null,
    string? Category = null,
    string? UserFriendlyMessage = null,
    string? ExecutionId = null,
    string? WorkflowId = null,
    string? EndpointId = null,
    string? RequestId = null,
    string? TraceId = null,
    string? SpanId = null,
    string? Status = null,
    string? ResolvedBy = null,
    DateTime? ResolvedOnUtc = null,
    string? DiagnosisAction = null,
    string? DiagnosisCause = null);

/// <summary>Search filter for the Monitoring → Errors screen (Phase 6A). All criteria optional and AND-combined.</summary>
public sealed record ErrorLogSearch(
    string? ErrorReferenceId = null,
    string? CorrelationId = null,
    string? ExecutionId = null,
    string? WorkflowId = null,
    string? EndpointId = null,
    string? Severity = null,
    string? Category = null,
    string? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Skip = 0,
    int Take = 200);

public sealed record ApiRequestLogDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string Method,
    string Url,
    int? StatusCode,
    long DurationMs,
    string? Error,
    string? CorrelationId,
    /// <summary>"Inbound" (a call made to this API) or "Outbound" (a call this system made to an EHR/destination).</summary>
    string Direction = "Outbound",
    /// <summary>Plain-language description of what this call was doing — see <c>ApiRequestStepDescriber</c>.
    /// Derived at read time from the three fields above, never stored.</summary>
    string Step = "");

public sealed record ExportHistoryDto(
    Guid Id,
    DateTime OccurredOnUtc,
    Guid? PipelineRunId,
    string DestinationName,
    string Format,
    int RowCount,
    long? FileSizeBytes,
    string Status,
    string? CorrelationId);

public sealed record NotificationHistoryDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string NotificationType,
    string Recipient,
    string? Subject,
    string Status,
    string? Error,
    string? CorrelationId);

public sealed record ValidationFailureDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string ResourceType,
    string? ResourceId,
    string WarningsJson,
    double? DataQualityScore,
    Guid? PipelineRunId,
    string? CorrelationId);

public sealed record EndpointHealthCheckDto(
    Guid Id,
    DateTime OccurredOnUtc,
    string EndpointName,
    string EndpointType,
    string Status,
    long LatencyMs,
    string? Message);

public sealed record SmartLaunchLogDto(
    Guid Id,
    DateTime OccurredOnUtc,
    Guid SourceConnectionId,
    string SourceName,
    string LaunchType,
    bool Success,
    string? FailureReason,
    string? GrantedScope,
    bool? PatientContextGranted,
    string? TokenCacheKeyHash,
    string? CorrelationId);

/// <summary>The currently-effective retention policy for one governance/operations data class.</summary>
public sealed record RetentionPolicyDto(
    string DataClass,
    int RetentionYears,
    bool Purgeable);

/// <summary>Where one governance event category is written, for the Log Settings screen.</summary>
public sealed record LogCategorySettingDto(string Category, string WritesTo, string Description);

/// <summary>
/// The real, currently-enforced logging configuration — deliberately read-only. PHI masking has no toggle
/// (disabling it would itself be a compliance regression); payload logging has no per-tenant opt-in today.
/// </summary>
public sealed record LogSettingsDto(
    bool PhiMaskingEnabled,
    bool PayloadLoggingImplemented,
    IReadOnlyList<LogCategorySettingDto> Categories);

/// <summary>One component's live health for the System Health screen — real measurements only, never a
/// placeholder row for something that isn't actually monitorable from this host (e.g. a remote Worker process).</summary>
public sealed record ComponentHealthDto(string Component, string Status, double? CpuPercent, long? MemoryBytes, string? Notes);

public sealed record SystemHealthDto(IReadOnlyList<ComponentHealthDto> Components);

/// <summary>Aggregated outbound-API-call stats for the current host process — see IApiMetricsSnapshotProvider.</summary>
public sealed record ApiAnalyticsDto(
    long TotalRequests,
    long TotalErrors,
    double ErrorRatePercent,
    IReadOnlyList<ApiEndpointStatDto> TopByCallCount,
    IReadOnlyList<ApiEndpointStatDto> SlowestByAverageDuration);

public sealed record ApiEndpointStatDto(
    string Method,
    string Url,
    long CallCount,
    double AverageDurationMs,
    double P95DurationMs,
    long ErrorCount);

/// <summary>One scheduled route's real next/last-run summary — the actual "per-route" view the Scheduler
/// screen always should have had, distinct from SchedulerHistory's per-dispatch-event log.</summary>
public sealed record SchedulerSummaryDto(
    Guid RouteId,
    string RouteName,
    string? ScheduleExpression,
    DateTime? NextRunUtc,
    DateTime? LastRunStartedUtc,
    long? LastRunDurationMs,
    string? LastRunStatus);

/// <summary>One field's lineage — structure only, deliberately no value (see IDataLineageService's remarks).</summary>
public sealed record DataLineageFieldDto(string SourceField, string? MappingRule, string DestinationColumn);

public sealed record DataLineageExportDto(string DestinationName, string Format, string Status, DateTime OccurredOnUtc);

public sealed record DataLineageDto(
    Guid ResourceRecordId,
    string ResourceType,
    string? SourceResourceId,
    string MappingProfileName,
    IReadOnlyList<DataLineageFieldDto> Fields,
    IReadOnlyList<DataLineageExportDto> Exports);

/// <summary>The result of a gated, audited reveal of one mapped field's actual (decrypted) value.</summary>
public sealed record LineageFieldValueDto(string TargetField, string? Value);

/// <summary>The most recent archive-before-purge run for one data class.</summary>
public sealed record ArchiveManifestDto(
    string DataClass,
    DateTime ArchivedThroughUtc,
    string FileLocation,
    int RecordCount,
    DateTime CreatedOnUtc);

/// <summary>
/// Correlation Search's result — everything across every governance/operations table (plus the Configured
/// Pipeline run header, if any) that shares one CorrelationId. <c>EndpointHealthCheck</c> is deliberately not
/// included: health checks are periodic, not per-run, so they don't carry a CorrelationId.
/// </summary>
public sealed record CorrelationSearchResultDto(
    string CorrelationId,
    ConfiguredPipelineRunDto? PipelineRun,
    IReadOnlyList<AuditLogDto> AuditLogs,
    IReadOnlyList<DataAccessLogDto> DataAccessLogs,
    IReadOnlyList<AuthenticationLogDto> AuthenticationLogs,
    IReadOnlyList<SecurityEventDto> SecurityEvents,
    IReadOnlyList<AuthorizationLogDto> AuthorizationLogs,
    IReadOnlyList<SchedulerHistoryDto> SchedulerHistory,
    IReadOnlyList<RetryHistoryDto> RetryHistory,
    IReadOnlyList<ErrorLogDto> Errors,
    IReadOnlyList<ApiRequestLogDto> ApiRequests,
    IReadOnlyList<ExportHistoryDto> Exports,
    IReadOnlyList<NotificationHistoryDto> Notifications,
    IReadOnlyList<ValidationFailureDto> ValidationFailures,
    IReadOnlyList<WorkflowRunSummaryDto> WorkflowRuns,
    IReadOnlyList<SmartLaunchLogDto> SmartLaunchLogs)
{
    public int TotalCount =>
        (PipelineRun is null ? 0 : 1) + AuditLogs.Count + DataAccessLogs.Count + AuthenticationLogs.Count +
        SecurityEvents.Count + AuthorizationLogs.Count + SchedulerHistory.Count + RetryHistory.Count + Errors.Count +
        ApiRequests.Count + Exports.Count + Notifications.Count + ValidationFailures.Count + WorkflowRuns.Count +
        SmartLaunchLogs.Count;
}

/// <summary>Runtime-plane (DAG) workflow run header, as surfaced by Correlation Search — a lighter shape than
/// the portal's full Execution History row since this is a cross-reference, not the primary listing.</summary>
public sealed record WorkflowRunSummaryDto(
    Guid Id,
    Guid WorkflowDefinitionId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? TriggeredBy,
    string? TriggerType,
    string? ErrorMessage);
