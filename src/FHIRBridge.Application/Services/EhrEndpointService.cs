using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed class EhrEndpointService : IEhrEndpointService
{
    private readonly IEhrEndpointRepository _repository;
    private readonly IOperationalAuditService _auditService;
    private readonly ICurrentUserService _currentUserService;

    public EhrEndpointService(
        IEhrEndpointRepository repository,
        IOperationalAuditService auditService,
        ICurrentUserService currentUserService)
    {
        _repository = repository;
        _auditService = auditService;
        _currentUserService = currentUserService;
    }

    public async Task<IReadOnlyList<EhrEndpointDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var endpoints = await _repository.GetAllAsync(cancellationToken);
        return endpoints.Select(EhrEndpointMapper.ToDto).ToArray();
    }

    public async Task<EhrEndpointDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await _repository.GetByIdAsync(id, cancellationToken);
        return endpoint is null ? null : EhrEndpointMapper.ToDto(endpoint);
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
            request.Status);

        await _repository.AddAsync(endpoint, cancellationToken);
        await AuditAsync("EhrEndpointCreated", $"EHR endpoint '{request.Name}' added.", cancellationToken);

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
            request.Status);

        await _repository.UpdateAsync(endpoint, cancellationToken);
        await AuditAsync("EhrEndpointUpdated", $"EHR endpoint '{request.Name}' updated.", cancellationToken);

        return EhrEndpointMapper.ToDto(endpoint);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await GetRequiredAsync(id, cancellationToken);
        await _repository.DeleteAsync(endpoint, cancellationToken);
        await AuditAsync("EhrEndpointDeleted", $"EHR endpoint '{endpoint.Name}' deleted.", cancellationToken);
    }

    private async Task<EhrEndpoint> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException("EhrEndpoint", id);

    private Task AuditAsync(string action, string message, CancellationToken cancellationToken) =>
        _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                null,
                null,
                null,
                null,
                null,
                null,
                action,
                "Completed",
                message,
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);

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
