using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.Abstractions.Sources;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>Maps an EHR Endpoint row to the values a run switches to. Only reached for runs that name an endpoint.</summary>
public sealed class EhrEndpointOverrideProvider : IEhrEndpointOverrideProvider
{
    private readonly IEhrEndpointRepository _repository;

    public EhrEndpointOverrideProvider(IEhrEndpointRepository repository)
    {
        _repository = repository;
    }

    public async Task<EhrEndpointOverride?> ResolveAsync(string ehrEndpointCode, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(ehrEndpointCode, out var id) || id == Guid.Empty)
        {
            return null;
        }

        var endpoint = await _repository.GetByIdAsync(id, cancellationToken);
        return endpoint is null
            ? null
            : new EhrEndpointOverride(
                endpoint.Id, endpoint.Name, endpoint.Vendor.ToString(), endpoint.FhirBaseUrl.Trim(),
                endpoint.TokenEndpoint, endpoint.ClientId, endpoint.KeyId, endpoint.JwksUrl, endpoint.PracticeId);
    }
}
