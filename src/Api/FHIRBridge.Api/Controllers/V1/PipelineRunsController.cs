using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/tenants/{tenantId:guid}/pipeline-runs")]
public sealed class PipelineRunsController : ControllerBase
{
    private readonly IConfiguredPipelineService _configuredPipelineService;
    private readonly IPipelineRunDispatcher _pipelineRunDispatcher;
    private readonly bool _hasSharedTransport;

    public PipelineRunsController(
        IConfiguredPipelineService configuredPipelineService,
        IPipelineRunDispatcher pipelineRunDispatcher,
        IConfiguration configuration)
    {
        _configuredPipelineService = configuredPipelineService;
        _pipelineRunDispatcher = pipelineRunDispatcher;

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
        Guid tenantId,
        [FromBody] StartConfiguredPipelineRunRequest request,
        CancellationToken cancellationToken)
    {
        // Bulk export ($export) can be long-running. When a shared transport is available, hand it to the Worker and
        // return immediately — the run is tracked via its PipelineRun record (visible in Runs) rather than blocking
        // the request. Without a shared transport, run synchronously so local/dev still works.
        if (request.UseBulkExport && _hasSharedTransport)
        {
            var messageId = $"bulkexport:{tenantId:N}:{Guid.NewGuid():N}";
            await _pipelineRunDispatcher.EnqueueAsync(
                new PipelineRunCommand(
                    tenantId,
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

        var pipelineRun = await _configuredPipelineService.StartAsync(
            tenantId,
            request,
            cancellationToken);

        return Created($"/api/v1/tenants/{tenantId}/pipeline-runs/{pipelineRun.Id}", pipelineRun);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ConfiguredPipelineRunDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecent(
        Guid tenantId,
        [FromQuery] int count,
        CancellationToken cancellationToken)
    {
        var pipelineRuns = await _configuredPipelineService.GetRecentAsync(
            tenantId,
            count <= 0 ? 100 : count,
            cancellationToken);

        return Ok(pipelineRuns);
    }

    [HttpPost("{pipelineRunId:guid}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(
        Guid tenantId,
        Guid pipelineRunId,
        CancellationToken cancellationToken)
    {
        await _configuredPipelineService.SetRunEnabledAsync(
            tenantId,
            pipelineRunId,
            false,
            cancellationToken);

        return NoContent();
    }
}
