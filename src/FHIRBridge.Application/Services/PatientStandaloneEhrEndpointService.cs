using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services;

public sealed class PatientStandaloneEhrEndpointService : IPatientStandaloneEhrEndpointService
{
    // The MyChart directory is in the hundreds of rows (see IEhrEndpointRepository.GetByEndpointTypeAsync) — cap the
    // response the same way EhrEndpointService.GetPublicEpicEndpointsAsync does, rather than assuming "small" like
    // the Epic-only set.
    private const int PublicMyChartEndpointsResultCap = 50;

    private readonly IEhrEndpointRepository _repository;

    public PatientStandaloneEhrEndpointService(IEhrEndpointRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<PublicEhrEpicEndpointDto>> GetPublicMyChartEndpointsAsync(
        string? search, CancellationToken cancellationToken)
    {
        var endpoints = await _repository.GetByEndpointTypeAsync(EhrEndpointType.MyChart, search, cancellationToken);
        return endpoints
            .Take(PublicMyChartEndpointsResultCap)
            .Select(x => new PublicEhrEpicEndpointDto(x.Id, x.Name, x.FhirBaseUrl, x.Status))
            .ToArray();
    }

    public async Task<bool> IsMyChartEndpointAsync(Guid ehrEndpointId, CancellationToken cancellationToken)
    {
        var endpoint = await _repository.GetByIdAsync(ehrEndpointId, cancellationToken);
        return endpoint is not null && endpoint.EndpointType == EhrEndpointType.MyChart;
    }
}
