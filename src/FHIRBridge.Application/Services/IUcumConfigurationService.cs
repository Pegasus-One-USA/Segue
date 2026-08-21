using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IUcumConfigurationService
{
    Task<UcumConfigurationDto> GetAsync(CancellationToken cancellationToken);
    Task<UcumConfigurationDto> UpdateAsync(UpdateUcumConfigurationRequest request, CancellationToken cancellationToken);
}
