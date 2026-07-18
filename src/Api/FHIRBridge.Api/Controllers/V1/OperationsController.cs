using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.SharedKernel.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

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

    public OperationsController(
        IGovernanceQueryService governanceQueryService,
        IQueueMonitorProvider queueMonitorProvider,
        IApiMetricsSnapshotProvider apiMetricsSnapshotProvider,
        ISystemHealthService systemHealthService,
        ISchedulerSummaryService schedulerSummaryService)
    {
        _governanceQueryService = governanceQueryService;
        _queueMonitorProvider = queueMonitorProvider;
        _apiMetricsSnapshotProvider = apiMetricsSnapshotProvider;
        _systemHealthService = systemHealthService;
        _schedulerSummaryService = schedulerSummaryService;
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
    [ProducesResponseType(typeof(IReadOnlyList<SchedulerHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSchedulerHistory(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetSchedulerHistoryAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("retry-history")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View retry history.")]
    [ProducesResponseType(typeof(IReadOnlyList<RetryHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRetryHistory(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetRetryHistoryAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("errors")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View error logs.")]
    [ProducesResponseType(typeof(IReadOnlyList<ErrorLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetErrorLogs(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetErrorLogsAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("api-requests")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View outbound API request logs.")]
    [ProducesResponseType(typeof(IReadOnlyList<ApiRequestLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetApiRequestLogs(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetApiRequestLogsAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("exports")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View export history.")]
    [ProducesResponseType(typeof(IReadOnlyList<ExportHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetExportHistory(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetExportHistoryAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("notifications")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View notification history.")]
    [ProducesResponseType(typeof(IReadOnlyList<NotificationHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotificationHistory(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetNotificationHistoryAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("validation-failures")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View data-quality validation failures.")]
    [ProducesResponseType(typeof(IReadOnlyList<ValidationFailureDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetValidationFailures(
        [FromQuery] string? correlationId, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetValidationFailuresAsync(correlationId, take, cancellationToken);
        return Ok(results);
    }

    [HttpGet("endpoint-health")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View endpoint health check history.")]
    [ProducesResponseType(typeof(IReadOnlyList<EndpointHealthCheckDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEndpointHealthChecks(
        [FromQuery] int take, CancellationToken cancellationToken)
    {
        var results = await _governanceQueryService.GetEndpointHealthChecksAsync(take, cancellationToken);
        return Ok(results);
    }
}
