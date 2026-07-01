using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IAnomalyDetectionService
{
    Task<RunAnomalySummaryDto> AnalyzeRunsAsync(
        Guid tenantId,
        int count,
        CancellationToken cancellationToken);
}
