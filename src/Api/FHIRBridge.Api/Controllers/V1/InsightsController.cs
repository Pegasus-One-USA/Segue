using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/insights")]
public sealed class InsightsController : ControllerBase
{
    private readonly IHedisMeasureReportService _measureReportService;
    private readonly IAnomalyDetectionService _anomalyDetectionService;

    public InsightsController(
        IHedisMeasureReportService measureReportService,
        IAnomalyDetectionService anomalyDetectionService)
    {
        _measureReportService = measureReportService;
        _anomalyDetectionService = anomalyDetectionService;
    }

    [HttpGet("measure-report")]
    [ProducesResponseType(typeof(HedisMeasureReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMeasureReport(
        [FromQuery] string? measureId,
        [FromQuery] DateTime? periodStartUtc,
        [FromQuery] DateTime? periodEndUtc,
        CancellationToken cancellationToken)
    {
        var end = periodEndUtc ?? DateTime.UtcNow;
        var start = periodStartUtc ?? end.AddDays(-30);
        var report = await _measureReportService.GenerateAsync(
            measureId ?? HedisMeasureReportService.PipelineSuccessMeasure,
            DateTime.SpecifyKind(start, DateTimeKind.Utc),
            DateTime.SpecifyKind(end, DateTimeKind.Utc),
            cancellationToken);

        return Ok(report);
    }

    [HttpGet("anomalies")]
    [ProducesResponseType(typeof(RunAnomalySummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAnomalies(
        [FromQuery] int count,
        CancellationToken cancellationToken)
    {
        var summary = await _anomalyDetectionService.AnalyzeRunsAsync(
            count,
            cancellationToken);

        return Ok(summary);
    }
}
