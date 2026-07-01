using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.SharedKernel.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Admin observability dashboard. The snapshot is computed from the durable pipeline-run store, so it reflects runs
/// executed in ANY host process (API manual/webhook runs AND Worker scheduled/queued runs) — not just this process's
/// in-memory metrics. For real-time, high-cardinality metrics across many instances, also export to an OTLP backend
/// via Observability:OtlpEndpoint.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/observability")]
public sealed class ObservabilityController : ControllerBase
{
    private readonly IPipelineRunMetricsService _metricsService;

    public ObservabilityController(IPipelineRunMetricsService metricsService)
    {
        _metricsService = metricsService;
    }

    [HttpGet("metrics")]
    [ProducesResponseType(typeof(MetricsSnapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMetrics(CancellationToken cancellationToken)
    {
        var snapshot = await _metricsService.GetProcessWideSnapshotAsync(200, cancellationToken);
        return Ok(snapshot);
    }
}
