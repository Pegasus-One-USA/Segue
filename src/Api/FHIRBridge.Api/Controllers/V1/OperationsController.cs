using FHIRBridge.Api.Operations;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Governance;
using FHIRBridge.Application.Security;
using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

public sealed record DeleteErrorsRequest(string Mode, DateTime? OlderThanUtc);

/// <summary>BeforeUtc: hide errors recorded before this instant from the Overview. Omitted = everything recorded so far.</summary>
public sealed record HideErrorsRequest(DateTime? BeforeUtc);

/// <summary>Read-only operations log screens (Scheduler History, Retry History, Errors, API Requests).</summary>
[ApiController]
[Authorize]
[Route("api/v1/operations")]
public sealed class OperationsController : ControllerBase
{
    private readonly IGovernanceQueryService _governanceQueryService;
    private readonly IQueueMonitorProvider _queueMonitorProvider;
    private readonly IApiMetricsSnapshotProvider _apiMetricsSnapshotProvider;
    private readonly ISystemHealthService _systemHealthService;
    private readonly ISchedulerSummaryService _schedulerSummaryService;
    private readonly IErrorResolutionService _errorResolutionService;
    private readonly IErrorScrubber _errorScrubber;
    private readonly ISystemSettingRepository _systemSettings;
    private readonly IErrorLogMaintenanceService _errorLogMaintenance;
    private readonly FHIRBridge.Governance.IGovernanceLogger _governanceLogger;

    /// <summary>Errors recorded before this instant are hidden from the Error Dashboard and its export. The ErrorLogs
    /// rows themselves are never deleted (append-only audit trail) and still appear on the Errors screen.</summary>
    private const string DashboardClearedKey = "ErrorDashboard:ClearedBeforeUtc";

    public OperationsController(
        IGovernanceQueryService governanceQueryService,
        IQueueMonitorProvider queueMonitorProvider,
        IApiMetricsSnapshotProvider apiMetricsSnapshotProvider,
        ISystemHealthService systemHealthService,
        ISchedulerSummaryService schedulerSummaryService,
        IErrorResolutionService errorResolutionService,
        IErrorScrubber errorScrubber,
        ISystemSettingRepository systemSettings,
        IErrorLogMaintenanceService errorLogMaintenance,
        FHIRBridge.Governance.IGovernanceLogger governanceLogger)
    {
        _governanceQueryService = governanceQueryService;
        _queueMonitorProvider = queueMonitorProvider;
        _apiMetricsSnapshotProvider = apiMetricsSnapshotProvider;
        _systemHealthService = systemHealthService;
        _schedulerSummaryService = schedulerSummaryService;
        _errorResolutionService = errorResolutionService;
        _errorScrubber = errorScrubber;
        _systemSettings = systemSettings;
        _errorLogMaintenance = errorLogMaintenance;
        _governanceLogger = governanceLogger;
    }

    /// <summary>Real per-route Next Run/Last Run summary — see ISchedulerSummaryService's remarks.</summary>
    [HttpGet("scheduler-summary")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View the scheduler's per-route next/last run summary.")]
    [ProducesResponseType(typeof(IReadOnlyList<SchedulerSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSchedulerSummary(CancellationToken cancellationToken)
    {
        var results = await _schedulerSummaryService.GetSummaryAsync(cancellationToken);
        return Ok(results);
    }

    /// <summary>In-process snapshot for this host — see IApiMetricsSnapshotProvider's remarks on cross-instance scope.</summary>
    [HttpGet("api-analytics")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View outbound API analytics.")]
    [ProducesResponseType(typeof(ApiAnalyticsDto), StatusCodes.Status200OK)]
    public IActionResult GetApiAnalytics()
    {
        var snapshot = _apiMetricsSnapshotProvider.GetSnapshot();
        var dto = new ApiAnalyticsDto(
            snapshot.TotalRequests,
            snapshot.TotalErrors,
            snapshot.ErrorRatePercent,
            snapshot.TopByCallCount.Select(ToDto).ToList(),
            snapshot.SlowestByAverageDuration.Select(ToDto).ToList());

        return Ok(dto);
    }

    /// <summary>Live CPU/memory for this process plus a real SQL Server connectivity probe — see
    /// ComponentHealthDto's remarks on why a remote Worker process isn't reported here.</summary>
    [HttpGet("system-health")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View live system health.")]
    [ProducesResponseType(typeof(SystemHealthDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSystemHealth(CancellationToken cancellationToken)
    {
        var result = await _systemHealthService.GetSystemHealthAsync(cancellationToken);
        return Ok(result);
    }

    private static ApiEndpointStatDto ToDto(ApiEndpointMetric metric) =>
        new(metric.Method, metric.Url, metric.CallCount, metric.AverageDurationMs, metric.P95DurationMs, metric.ErrorCount);

    /// <summary>Real queue depth for the configured messaging transport — see IQueueMonitorProvider's remarks
    /// for why an entry may report UnavailableReason instead of counts.</summary>
    [HttpGet("queue-monitor")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View message queue depth and dead-letter counts.")]
    [ProducesResponseType(typeof(IReadOnlyList<QueueDepthDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueMonitor(CancellationToken cancellationToken)
    {
        var results = await _queueMonitorProvider.GetQueueDepthsAsync(cancellationToken);
        return Ok(results);
    }

    [HttpGet("scheduler-history")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View scheduler dispatch history.")]
    [ProducesResponseType(typeof(PagedResult<SchedulerHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSchedulerHistory(
        [FromQuery] string? correlationId, [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetSchedulerHistoryAsync(correlationId, skip, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("retry-history")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View retry history.")]
    [ProducesResponseType(typeof(PagedResult<RetryHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRetryHistory(
        [FromQuery] string? correlationId, [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetRetryHistoryAsync(correlationId, skip, take, cancellationToken);
        return Ok(results);
    }

    /// <summary>Phase 6A – Monitoring → Errors search. All criteria optional and AND-combined; supports search
    /// by Error Reference ID, Correlation ID, Execution ID, Workflow, Endpoint, Severity, Category, Status, and
    /// a date range.</summary>
    [HttpGet("errors")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View error logs.")]
    [ProducesResponseType(typeof(PagedResult<ErrorLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetErrorLogs(
        [FromQuery] string? correlationId,
        [FromQuery] int skip,
        [FromQuery] int take,
        [FromQuery] string? errorReferenceId,
        [FromQuery] string? executionId,
        [FromQuery] string? workflowId,
        [FromQuery] string? endpointId,
        [FromQuery] string? severity,
        [FromQuery] string? category,
        [FromQuery] string? status,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        var search = new ErrorLogSearch(
            errorReferenceId, correlationId, executionId, workflowId, endpointId,
            severity, category, status, fromUtc, toUtc, skip, take);
        var results = await _governanceQueryService.SearchErrorLogsAsync(search, cancellationToken);
        return Ok(results);
    }

    /// <summary>Error Dashboard – counts, daily trend and recurring-error signatures for a period (default last 14 days).</summary>
    [HttpGet("errors/dashboard")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View the error dashboard.")]
    [ProducesResponseType(typeof(ErrorDashboardDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetErrorDashboard(
        [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc,
        [FromQuery] string? severity, [FromQuery] string? category, CancellationToken cancellationToken)
    {
        var cleared = await GetClearedAtAsync(cancellationToken);
        var (from, to) = ResolveRange(fromUtc, toUtc, cleared);
        var dashboard = await _governanceQueryService.GetErrorDashboardAsync(from, to, severity, category, cancellationToken);
        return Ok(dashboard with { ClearedAtUtc = cleared });
    }

    /// <summary>Error Overview – "hide old errors": hides every error recorded before the chosen date (default: everything
    /// recorded so far) from the Overview and its export. Nothing is deleted; All errors and the audit trail keep every row.</summary>
    [HttpPost("errors/dashboard/clear")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Clear the error dashboard.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearErrorDashboard([FromBody] HideErrorsRequest? request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var before = (request?.BeforeUtc is { } requested ? AsUtc(requested) : now);
        if (before > now)
        {
            before = now;
        }

        await _systemSettings.UpsertAsync(
            DashboardClearedKey,
            before.ToString("O"),
            "Errors recorded before this time are hidden from the Error Dashboard (cleared by an admin).",
            cancellationToken);
        return NoContent();
    }

    /// <summary>Error log – PERMANENTLY delete entries and give the space back to the database. <c>mode</c> is
    /// <c>all</c> (everything) or <c>olderThan</c> (entries recorded before <c>olderThanUtc</c>). Unlike the dashboard's
    /// "hide old errors", this cannot be undone. Recorded as a security event.</summary>
    [HttpPost("errors/delete")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Delete, description: "Permanently delete error log entries.")]
    [ProducesResponseType(typeof(ErrorLogDeleteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteErrors([FromBody] DeleteErrorsRequest request, CancellationToken cancellationToken)
    {
        var all = string.Equals(request.Mode, "all", StringComparison.OrdinalIgnoreCase);
        var older = string.Equals(request.Mode, "olderThan", StringComparison.OrdinalIgnoreCase);
        if (!all && !older)
        {
            return BadRequest(new { error = "mode must be 'all' or 'olderThan'." });
        }

        DateTime? cutoff = null;
        if (older)
        {
            if (request.OlderThanUtc is null)
            {
                return BadRequest(new { error = "Choose the date to delete before." });
            }

            cutoff = AsUtc(request.OlderThanUtc.Value);
            if (cutoff > DateTime.UtcNow)
            {
                return BadRequest(new { error = "The date must be in the past." });
            }
        }

        var scope = all ? "All entries" : "Entries before " + cutoff!.Value.ToString("u");

        // Audit FIRST: the delete is irreversible, so it must never happen without a record that it was requested. If this
        // write fails the request fails and nothing is deleted.
        await _governanceLogger.LogSecurityEventAsync(
            new FHIRBridge.Governance.SecurityEventEntry(
                "ErrorLogDeleteRequested", "Warning", User.Identity?.Name, $"Permanent delete requested: {scope}."),
            cancellationToken);

        var result = await _errorLogMaintenance.DeleteAndReclaimAsync(cutoff, cancellationToken);
        if (all)
        {
            // Nothing left to hide, so the dashboard's "hide old errors" marker would only be confusing.
            await _systemSettings.DeleteAsync(DashboardClearedKey, cancellationToken);
        }

        try
        {
            await _governanceLogger.LogSecurityEventAsync(
                new FHIRBridge.Governance.SecurityEventEntry(
                    "ErrorLogDeleted", "Warning", User.Identity?.Name,
                    $"{scope} permanently deleted: {result.Deleted} removed; space released: {result.SpaceReclaimed}."),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The request record above already exists; a failure of the completion record must not turn a finished
            // delete into an error response.
            FHIRBridge.Governance.SwallowedError.Report(exception, "ErrorLog.delete completion audit");
        }

        return Ok(result);
    }

    /// <summary>Error Dashboard – undo a previous clear so every recorded error is shown again.</summary>
    [HttpPost("errors/dashboard/restore")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Show cleared errors on the dashboard again.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RestoreErrorDashboard(CancellationToken cancellationToken)
    {
        await _systemSettings.DeleteAsync(DashboardClearedKey, cancellationToken);
        return NoContent();
    }

    /// <summary>Error Dashboard – downloadable, PHI-scrubbed report (json or csv) a client can send to the vendor
    /// for support. Capped at ErrorReportBuilder.MaxRows rows, newest first.</summary>
    [HttpGet("errors/export")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "Export the error report.")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportErrors(
        [FromQuery] string? format,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? severity,
        [FromQuery] string? category,
        [FromQuery] bool includeStackTrace = true,
        [FromQuery] bool includeCorrelation = true,
        CancellationToken cancellationToken = default)
    {
        var asCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        if (!asCsv && !string.IsNullOrWhiteSpace(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "format must be 'json' or 'csv'." });
        }

        var (from, to) = ResolveRange(fromUtc, toUtc, await GetClearedAtAsync(cancellationToken));
        var summary = await _governanceQueryService.GetErrorDashboardAsync(from, to, severity, category, cancellationToken);

        const int pageSize = 500;
        var errors = new List<ErrorLogDto>();
        var truncated = false;
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await _governanceQueryService.SearchErrorLogsAsync(
                new ErrorLogSearch(Severity: severity, Category: category, FromUtc: from, ToUtc: to, Skip: skip, Take: pageSize),
                cancellationToken);
            errors.AddRange(page.Items);
            if (page.Items.Count < pageSize || errors.Count >= page.TotalCount) break;
            if (errors.Count >= ErrorReportBuilder.MaxRows)
            {
                truncated = true;
                break;
            }
        }

        if (errors.Count > ErrorReportBuilder.MaxRows)
        {
            errors.RemoveRange(ErrorReportBuilder.MaxRows, errors.Count - ErrorReportBuilder.MaxRows);
            truncated = true;
        }

        var version = typeof(OperationsController).Assembly.GetName().Version?.ToString() ?? "unknown";
        var applied = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(severity)) applied["severity"] = severity;
        if (!string.IsNullOrWhiteSpace(category)) applied["category"] = category;
        applied["includeStackTrace"] = includeStackTrace.ToString();
        applied["includeCorrelation"] = includeCorrelation.ToString();

        // Run timelines (summary-only, no audit/auth/security logs) for the distinct runs behind the exported errors.
        var correlations = new Dictionary<string, ErrorCorrelationDto>();
        var correlationsTruncated = false;
        if (includeCorrelation)
        {
            const int maxRuns = 50;
            var ids = errors.Select(e => e.CorrelationId).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).ToList();
            correlationsTruncated = ids.Count > maxRuns;
            var runs = await _governanceQueryService.GetCorrelationRunSummariesAsync(ids.Take(maxRuns).ToList()!, cancellationToken);
            foreach (var (id, result) in runs)
            {
                correlations[id] = ErrorCorrelationBuilder.Build(result, _errorScrubber, includeSecurityLogs: false);
            }
        }

        var report = ErrorReportBuilder.Build(
            summary, errors, truncated, includeStackTrace, version, _errorScrubber, applied, correlations, correlationsTruncated);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        return asCsv
            ? File(ErrorReportBuilder.ToCsv(report), "text/csv; charset=utf-8", $"fhirbridge-error-report-{stamp}.csv")
            : File(ErrorReportBuilder.ToJson(report), "application/json", $"fhirbridge-error-report-{stamp}.json");
    }

    /// <summary>Error detail – the run timeline around one correlation id (shown from the Error Dashboard popup).
    /// Summaries only: no audit payloads, e-mails, IPs or patient identifiers.</summary>
    [HttpGet("errors/correlation/{correlationId}")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View an error's correlation details.")]
    [ProducesResponseType(typeof(ErrorCorrelationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetErrorCorrelation(string correlationId, CancellationToken cancellationToken)
    {
        var result = await _governanceQueryService.GetCorrelationSearchResultAsync(correlationId, cancellationToken);
        return Ok(ErrorCorrelationBuilder.Build(result, _errorScrubber, includeSecurityLogs: true));
    }

    /// <summary>Treats a timestamp that carries no offset as UTC (as the portal sends it) instead of letting
    /// <c>ToUniversalTime()</c> assume the server's local zone and shift it.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private async Task<DateTime?> GetClearedAtAsync(CancellationToken cancellationToken)
    {
        var setting = await _systemSettings.GetByKeyAsync(DashboardClearedKey, cancellationToken);
        return setting is not null
            && DateTime.TryParse(setting.Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
    }

    private static (DateTime From, DateTime To) ResolveRange(DateTime? fromUtc, DateTime? toUtc, DateTime? clearedAtUtc = null)
    {
        var to = toUtc is { } t ? AsUtc(t) : DateTime.UtcNow;
        var from = fromUtc is { } f ? AsUtc(f) : to.AddDays(-14);
        if (from > to) (from, to) = (to, from);
        if (clearedAtUtc is { } cleared && cleared > from) from = cleared <= to ? cleared : to;
        return (from, to);
    }

    /// <summary>Phase 6A – mark a captured error Resolved. The immutable ErrorLog record is never mutated;
    /// only the separate resolution triage row changes.</summary>
    [HttpPost("errors/{errorReferenceId}/resolve")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Resolve a captured error.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResolveError(
        string errorReferenceId, [FromBody] ResolveErrorRequest? request, CancellationToken cancellationToken)
    {
        var resolved = await _errorResolutionService.ResolveAsync(
            errorReferenceId, User.Identity?.Name ?? "system", request?.Notes, cancellationToken);
        return resolved ? NoContent() : NotFound();
    }

    /// <summary>Phase 6A – reopen a previously resolved error.</summary>
    [HttpPost("errors/{errorReferenceId}/reopen")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Reopen a resolved error.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReopenError(string errorReferenceId, CancellationToken cancellationToken)
    {
        var reopened = await _errorResolutionService.ReopenAsync(errorReferenceId, cancellationToken);
        return reopened ? NoContent() : NotFound();
    }

    public sealed record ResolveErrorRequest(string? Notes);

    [HttpGet("api-requests")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View outbound API request logs.")]
    [ProducesResponseType(typeof(PagedResult<ApiRequestLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetApiRequestLogs(
        [FromQuery] string? correlationId, [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetApiRequestLogsAsync(correlationId, skip, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("exports")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View export history.")]
    [ProducesResponseType(typeof(PagedResult<ExportHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetExportHistory(
        [FromQuery] string? correlationId, [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetExportHistoryAsync(correlationId, skip, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("notifications")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View notification history.")]
    [ProducesResponseType(typeof(PagedResult<NotificationHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotificationHistory(
        [FromQuery] string? correlationId, [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetNotificationHistoryAsync(correlationId, skip, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("validation-failures")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View data-quality validation failures.")]
    [ProducesResponseType(typeof(PagedResult<ValidationFailureDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetValidationFailures(
        [FromQuery] string? correlationId, [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetValidationFailuresAsync(correlationId, skip, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("endpoint-health")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View endpoint health check history.")]
    [ProducesResponseType(typeof(PagedResult<EndpointHealthCheckDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEndpointHealthChecks(
        [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetEndpointHealthChecksAsync(skip, take, cancellationToken);
        return Ok(results);
    }
}
