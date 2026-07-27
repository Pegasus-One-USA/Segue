using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/pipeline-runs")]
public sealed class PipelineRunsController : ControllerBase
{
    private readonly IConfiguredPipelineService _configuredPipelineService;
    private readonly IPipelineRunDispatcher _pipelineRunDispatcher;
    private readonly IPipelineRunRouteExecutionRepository _routeExecutionRepository;
    private readonly IExecutionResourceHistoryRecorder _resourceHistoryRecorder;
    private readonly ICurrentUserService _currentUserService;
    private readonly bool _hasSharedTransport;

    public PipelineRunsController(
        IConfiguredPipelineService configuredPipelineService,
        IPipelineRunDispatcher pipelineRunDispatcher,
        IPipelineRunRouteExecutionRepository routeExecutionRepository,
        IExecutionResourceHistoryRecorder resourceHistoryRecorder,
        ICurrentUserService currentUserService,
        IConfiguration configuration)
    {
        _configuredPipelineService = configuredPipelineService;
        _pipelineRunDispatcher = pipelineRunDispatcher;
        _routeExecutionRepository = routeExecutionRepository;
        _resourceHistoryRecorder = resourceHistoryRecorder;
        _currentUserService = currentUserService;

        // A shared transport (RabbitMQ / Azure Service Bus) lets the Worker pick up long-running jobs; InMemory cannot
        // cross the API→Worker process boundary, so those fall back to synchronous execution.
        var provider = configuration["Messaging:Provider"];
        _hasSharedTransport = !string.IsNullOrWhiteSpace(provider)
            && !string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ConfiguredPipelineRunDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Start(
        [FromBody] StartConfiguredPipelineRunRequest request,
        CancellationToken cancellationToken)
    {
        // Reuse the ambient request's correlation id (the same one ApiRequestLoggingHandler already stamps on
        // every outbound HTTP call this run triggers) when the caller didn't supply one explicitly — otherwise
        // every log row this run produces (ValidationFailureLogs, ApiRequestLogs, ExportHistory, ...) carries no
        // correlation id at all, making it untraceable. Same fix as WorkflowEndpoints.cs's /run endpoint.
        var correlationId = request.CorrelationId ?? _currentUserService.CurrentUser.CorrelationId;
        if (correlationId != request.CorrelationId)
        {
            request = request with { CorrelationId = correlationId };
        }

        // Bulk export ($export) can be long-running. When a shared transport is available, hand it to the Worker and
        // return immediately — the run is tracked via its PipelineRun record (visible in Runs) rather than blocking
        // the request. Without a shared transport, run synchronously so local/dev still works.
        if (request.UseBulkExport && _hasSharedTransport)
        {
            var messageId = $"bulkexport:{Guid.NewGuid():N}";
            await _pipelineRunDispatcher.EnqueueAsync(
                new PipelineRunCommand(
                    request.ResourceTypes?.ToList() ?? [],
                    request.RunDueSchedulesOnly,
                    request.ScheduledAtUtc,
                    request.TriggeredBy,
                    request.CorrelationId,
                    messageId)
                {
                    RouteIds = request.RouteIds?.ToList(),
                    UseBulkExport = true
                },
                cancellationToken);

            return Accepted(new { status = "queued", mode = "bulk-export", messageId });
        }

        // This is the one call path with an HTTP response to carry a Download-mode destination's bytes back
        // through — every other trigger (schedule, webhook, queued/bulk run) leaves this false by default.
        var pipelineRun = await _configuredPipelineService.StartAsync(
            request with { AllowInlineDownload = true },
            cancellationToken);

        if (pipelineRun.InlineDownload is { } file)
        {
            return File(file.Content, file.ContentType, file.FileName);
        }

        return Created($"/api/v1/pipeline-runs/{pipelineRun.Id}", pipelineRun);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ConfiguredPipelineRunDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecent(
        [FromQuery] int count,
        CancellationToken cancellationToken)
    {
        var pipelineRuns = await _configuredPipelineService.GetRecentAsync(
            count <= 0 ? 100 : count,
            cancellationToken);

        return Ok(pipelineRuns);
    }

    [HttpPost("{pipelineRunId:guid}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(
        Guid pipelineRunId,
        CancellationToken cancellationToken)
    {
        await _configuredPipelineService.SetRunEnabledAsync(
            pipelineRunId,
            false,
            cancellationToken);

        return NoContent();
    }

    // ── Execution History (per-route run detail) ────────────────────────────────

    [HttpGet("route-executions")]
    [ProducesResponseType(typeof(PagedResult<PipelineRunRouteExecutionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRouteExecutions(
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] string? triggeredBy,
        [FromQuery] string? search,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _routeExecutionRepository.GetPagedAsync(
            new PipelineRunRouteExecutionFilter(status, source, triggeredBy, search),
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 25 : pageSize,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("route-executions/{routeExecutionId:guid}")]
    [ProducesResponseType(typeof(PipelineRunRouteExecutionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRouteExecution(
        Guid routeExecutionId,
        CancellationToken cancellationToken)
    {
        var execution = await _routeExecutionRepository.GetByIdAsync(routeExecutionId, cancellationToken);
        return execution is null ? NotFound() : Ok(execution);
    }

    /// <summary>
    /// Drill-down into a route execution's per-resource fetch/normalize/map/store history. PHI-free: returns only
    /// per-stage status, timing, warnings, data-quality score, and the master patient id — never the raw
    /// fetched/normalized/mapped payloads. To view an individual decrypted field value, use the gated + audited
    /// reveal on the Data Lineage screen (DataLineageController.RevealFieldValue), which requires the stricter
    /// Payload/View permission and writes a DataAccessLog per reveal.
    /// </summary>
    [HttpGet("route-executions/{routeExecutionId:guid}/resources")]
    [ProducesResponseType(typeof(PagedResult<PipelineRunResourceHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRouteExecutionResources(
        Guid routeExecutionId,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _resourceHistoryRecorder.GetPagedAsync(
            routeExecutionId,
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 25 : pageSize,
            cancellationToken);

        return Ok(result);
    }
}
