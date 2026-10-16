using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IApiClientService
{
    Task<PagedResult<ApiClientDto>> GetPagedAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Creates a client. The plaintext secret is present in the result exactly once.</summary>
    Task<ApiClientCredentialDto> CreateAsync(CreateApiClientRequest request, CancellationToken cancellationToken);

    /// <summary>Rotates the secret, keeping ClientId stable. The new plaintext secret is present in the
    /// result exactly once — it cannot be recovered afterward.</summary>
    Task<ApiClientCredentialDto> RegenerateSecretAsync(Guid id, CancellationToken cancellationToken);

    Task<ApiClientDto> UpdateAsync(Guid id, UpdateApiClientRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<ApiClientDto> AddReturnUrlAsync(Guid id, AddApiClientReturnUrlRequest request, CancellationToken cancellationToken);

    Task<ApiClientDto> RemoveReturnUrlAsync(Guid id, Guid returnUrlId, CancellationToken cancellationToken);
}
