using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface ILoincConfigurationService
{
    Task<LoincConfigurationDto> GetAsync(CancellationToken cancellationToken);
    Task<LoincConfigurationDto> UpdateAsync(UpdateLoincConfigurationRequest request, CancellationToken cancellationToken);
}
