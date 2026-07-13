using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IEhrEndpointService
{
    Task<IReadOnlyList<EhrEndpointDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<EhrEndpointDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<EhrEndpointDto> AddAsync(CreateEhrEndpointRequest request, CancellationToken cancellationToken);

    Task<EhrEndpointDto> UpdateAsync(Guid id, CreateEhrEndpointRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
