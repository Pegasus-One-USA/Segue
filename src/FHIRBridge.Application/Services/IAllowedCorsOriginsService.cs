using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IAllowedCorsOriginsService
{
    Task<IReadOnlyList<AllowedCorsOriginDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<AllowedCorsOriginDto> AddAsync(CreateAllowedCorsOriginRequest request, CancellationToken cancellationToken);

    Task<AllowedCorsOriginDto> UpdateAsync(Guid id, UpdateAllowedCorsOriginRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
