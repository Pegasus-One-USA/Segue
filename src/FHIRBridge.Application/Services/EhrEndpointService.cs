using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed class EhrEndpointService : IEhrEndpointService
{
    private readonly IEhrEndpointRepository _repository;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;

    public EhrEndpointService(IEhrEndpointRepository repository, IUserDisplayNameResolver userDisplayNameResolver)
    {
        _repository = repository;
        _userDisplayNameResolver = userDisplayNameResolver;
    }

    public async Task<IReadOnlyList<EhrEndpointDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var endpoints = await _repository.GetAllAsync(cancellationToken);
        var dtos = endpoints.Select(EhrEndpointMapper.ToDto).ToArray();
        return await ResolveDisplayNamesAsync(dtos, cancellationToken);
    }

    /// <summary>Resolves each DTO's CreatedBy/ModifiedBy (a stored Users.Id GUID, or an older/system string
    /// predating that) to a display name, in one batched lookup rather than one per row.</summary>
    private async Task<IReadOnlyList<EhrEndpointDto>> ResolveDisplayNamesAsync(
        IReadOnlyList<EhrEndpointDto> dtos, CancellationToken cancellationToken)
    {
        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.SelectMany(dto => new[] { dto.CreatedBy, dto.ModifiedBy }), cancellationToken);

        return dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        }).ToArray();
    }

    public async Task<PagedResult<EhrEndpointDto>> GetPagedAsync(
        EhrEndpointFilter filter, bool? sortDescending, int page, int pageSize, CancellationToken cancellationToken)
    {
        var paged = await _repository.GetPagedAsync(filter, sortDescending, page, pageSize, cancellationToken);
        var dtos = await ResolveDisplayNamesAsync(paged.Items.Select(EhrEndpointMapper.ToDto).ToArray(), cancellationToken);
        return new PagedResult<EhrEndpointDto>(dtos, paged.TotalCount, paged.Page, paged.PageSize);
    }

    public async Task<IReadOnlyList<PublicEhrEndpointDto>> GetPublicEndpointsAsync(
        EhrEndpointType endpointType, string? search, CancellationToken cancellationToken)
    {
        var endpoints = await _repository.GetPublicAsync(endpointType, search, cancellationToken);
        return endpoints
            .Take(PublicEndpointsResultCap)
            .Select(x => new PublicEhrEndpointDto(x.Id, x.Name, x.FhirBaseUrl, x.Status))
            .ToArray();
    }

    // The directory can be in the hundreds of rows (e.g. the MyChart set alone) — cap the response rather than
    // assume "small" stays true.
    private const int PublicEndpointsResultCap = 50;

    public async Task<EhrEndpointDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await _repository.GetByIdAsync(id, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var resolved = await ResolveDisplayNamesAsync([EhrEndpointMapper.ToDto(endpoint)], cancellationToken);
        return resolved[0];
    }

    public async Task<bool> IsKnownEndpointAsync(Guid ehrEndpointId, EhrEndpointType endpointType, CancellationToken cancellationToken)
    {
        var endpoint = await _repository.GetByIdAsync(ehrEndpointId, cancellationToken);
        return endpoint is not null && endpoint.EndpointType == endpointType;
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

        var resolved = await ResolveDisplayNamesAsync([EhrEndpointMapper.ToDto(endpoint)], cancellationToken);
        return resolved[0];
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

        var resolved = await ResolveDisplayNamesAsync([EhrEndpointMapper.ToDto(endpoint)], cancellationToken);
        return resolved[0];
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
