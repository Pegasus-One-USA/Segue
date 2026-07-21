using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IAppSecretsAdminService
{
    Task<IReadOnlyList<AppSecretDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<AppSecretDto> RegenerateAsync(string secretName, CancellationToken cancellationToken);
}
