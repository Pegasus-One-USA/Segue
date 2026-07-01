using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IHedisMeasureReportService
{
    Task<HedisMeasureReportDto> GenerateAsync(
        Guid tenantId,
        string measureId,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken cancellationToken);
}
