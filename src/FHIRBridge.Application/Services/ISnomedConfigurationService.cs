using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface ISnomedConfigurationService
{
    Task<SnomedConfigurationDto> GetAsync(CancellationToken cancellationToken);
    Task<SnomedConfigurationDto> UpdateAsync(UpdateSnomedConfigurationRequest request, CancellationToken cancellationToken);
}
