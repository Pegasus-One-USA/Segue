using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface INdcConfigurationService
{
    Task<NdcConfigurationDto> GetAsync(CancellationToken cancellationToken);
    Task<NdcConfigurationDto> UpdateAsync(UpdateNdcConfigurationRequest request, CancellationToken cancellationToken);
}
