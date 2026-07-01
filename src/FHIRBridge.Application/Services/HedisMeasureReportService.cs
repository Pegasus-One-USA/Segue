using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public sealed class HedisMeasureReportService : IHedisMeasureReportService
{
    public const string PipelineSuccessMeasure = "FHIRBridge-PIPELINE-SUCCESS";
    public const string ResourceWriteMeasure = "FHIRBridge-RESOURCE-WRITE";

    private readonly IConfiguredPipelineRunRepository _pipelineRunRepository;

    public HedisMeasureReportService(IConfiguredPipelineRunRepository pipelineRunRepository)
    {
        _pipelineRunRepository = pipelineRunRepository;
    }

    public async Task<HedisMeasureReportDto> GenerateAsync(
        Guid tenantId,
        string measureId,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken cancellationToken)
    {
        var normalizedMeasureId = string.IsNullOrWhiteSpace(measureId)
            ? PipelineSuccessMeasure
            : measureId.Trim();
        var recentRuns = await _pipelineRunRepository.GetRecentAsync(tenantId, 1000, cancellationToken);
        var runs = recentRuns
            .Where(run => run.StartedOnUtc >= periodStartUtc && run.StartedOnUtc <= periodEndUtc)
            .ToList();

        var group = string.Equals(normalizedMeasureId, ResourceWriteMeasure, StringComparison.OrdinalIgnoreCase)
            ? BuildResourceWriteGroup(runs)
            : BuildPipelineSuccessGroup(runs);

        return new HedisMeasureReportDto(
            "MeasureReport",
            $"measurereport-{tenantId:N}-{normalizedMeasureId.ToLowerInvariant()}",
            "complete",
            "summary",
            normalizedMeasureId,
            new HedisMeasurePeriodDto(periodStartUtc, periodEndUtc),
            [group]);
    }

    private static HedisMeasureGroupDto BuildPipelineSuccessGroup(IReadOnlyCollection<ConfiguredPipelineRunDto> runs)
    {
        var denominator = runs.Count;
        var numerator = runs.Count(run => string.Equals(run.Status, "Completed", StringComparison.OrdinalIgnoreCase));
        return BuildGroup("pipeline-success", "Pipeline run success rate", denominator, numerator);
    }

    private static HedisMeasureGroupDto BuildResourceWriteGroup(IReadOnlyCollection<ConfiguredPipelineRunDto> runs)
    {
        var denominator = runs.Sum(run => run.ExtractedResourceCount);
        var numerator = runs.Sum(run => run.WrittenRecordCount);
        return BuildGroup("resource-write", "Extracted resources written to destination", denominator, numerator);
    }

    private static HedisMeasureGroupDto BuildGroup(
        string code,
        string display,
        int denominator,
        int numerator)
    {
        var score = denominator == 0 ? 0 : Math.Round((decimal)numerator / denominator, 4);
        return new HedisMeasureGroupDto(
            code,
            display,
            denominator,
            numerator,
            score,
            [
                new HedisMeasurePopulationDto("denominator", denominator),
                new HedisMeasurePopulationDto("numerator", numerator)
            ]);
    }
}
