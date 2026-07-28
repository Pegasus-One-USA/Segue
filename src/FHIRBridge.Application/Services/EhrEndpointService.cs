using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed class EhrEndpointService : IEhrEndpointService
{
    private readonly IEhrEndpointRepository _repository;

    public EhrEndpointService(IEhrEndpointRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<EhrEndpointDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var endpoints = await _repository.GetAllAsync(cancellationToken);
        return endpoints.Select(EhrEndpointMapper.ToDto).ToArray();
    }

    public async Task<IReadOnlyList<PublicEhrEndpointDto>> GetPublicEndpointsAsync(
        string? search, CancellationToken cancellationToken)
    {
        var endpoints = await _repository.GetPublicAsync(search, cancellationToken);
        return endpoints
            .Take(PublicEndpointsResultCap)
            .Select(x => new PublicEhrEndpointDto(x.Id, x.Name, x.FhirBaseUrl, x.Status))
            .ToArray();
    }

    // The directory can be in the hundreds of rows (e.g. the MyChart set alone) — cap the response rather than
    // assume "small" stays true now that this lists every EndpointType together.
    private const int PublicEndpointsResultCap = 50;

    public async Task<EhrEndpointDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await _repository.GetByIdAsync(id, cancellationToken);
        return endpoint is null ? null : EhrEndpointMapper.ToDto(endpoint);
    }

    public async Task<bool> IsKnownEndpointAsync(Guid ehrEndpointId, CancellationToken cancellationToken)
    {
        var endpoint = await _repository.GetByIdAsync(ehrEndpointId, cancellationToken);
        return endpoint is not null;
    }

    public async Task<EhrEndpointDto> AddAsync(CreateEhrEndpointRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var endpoint = new EhrEndpoint(
            request.Vendor,
            request.VendorEndpointId,
            request.Name,
            request.FhirBaseUrl,
            request.FormatType,
            request.Status,
            request.EndpointType);

        await _repository.AddAsync(endpoint, cancellationToken);

        return EhrEndpointMapper.ToDto(endpoint);
    }

    public async Task<EhrEndpointDto> UpdateAsync(Guid id, CreateEhrEndpointRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var endpoint = await GetRequiredAsync(id, cancellationToken);
        endpoint.Update(
            request.Vendor,
            request.VendorEndpointId,
            request.Name,
            request.FhirBaseUrl,
            request.FormatType,
            request.Status,
            request.EndpointType);

        await _repository.UpdateAsync(endpoint, cancellationToken);

        return EhrEndpointMapper.ToDto(endpoint);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await GetRequiredAsync(id, cancellationToken);
        await _repository.DeleteAsync(endpoint, cancellationToken);
    }

    private async Task<EhrEndpoint> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException("EhrEndpoint", id);

    private static void ValidateRequest(CreateEhrEndpointRequest request)
    {
        var requiredFields = new (string Value, string Message)[]
        {
            (request.VendorEndpointId, "Vendor endpoint id is required."),
            (request.Name, "Endpoint name is required."),
            (request.FhirBaseUrl, "FHIR base URL is required."),
            (request.FormatType, "Format type is required."),
            (request.Status, "Status is required."),
        };

        foreach (var (value, message) in requiredFields)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
