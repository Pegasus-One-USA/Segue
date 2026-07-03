using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IHedisMeasureReportService
{
    Task<HedisMeasureReportDto> GenerateAsync(
        string measureId,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken cancellationToken);
}
