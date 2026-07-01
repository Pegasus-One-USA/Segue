using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface ITenantRegistrationService
{
    Task<RegisterTenantResponse> RegisterAsync(RegisterTenantRequest request, CancellationToken cancellationToken);
}
