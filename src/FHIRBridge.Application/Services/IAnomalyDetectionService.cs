using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IAnomalyDetectionService
{
    Task<RunAnomalySummaryDto> AnalyzeRunsAsync(
        int count,
        CancellationToken cancellationToken);
}
