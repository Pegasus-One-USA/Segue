namespace FHIRBridge.Application.DTOs;

public sealed record RunAnomalySummaryDto(
    Guid TenantId,
    DateTime EvaluatedOnUtc,
    int EvaluatedRunCount,
    IReadOnlyList<RunAnomalyDto> Anomalies);

public sealed record RunAnomalyDto(
    Guid PipelineRunId,
    string Severity,
    string Category,
    string Message,
    decimal Score,
    DateTime StartedOnUtc);
