using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IRxNormConfigurationService
{
    Task<RxNormConfigurationDto> GetAsync(CancellationToken cancellationToken);
    Task<RxNormConfigurationDto> UpdateAsync(UpdateRxNormConfigurationRequest request, CancellationToken cancellationToken);
}
