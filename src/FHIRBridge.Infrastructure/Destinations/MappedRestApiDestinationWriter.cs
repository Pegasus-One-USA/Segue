using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

public sealed class MappedRestApiDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedRestApiDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<int> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        var endpoint = destination.Target;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        }

        var httpClient = _httpClientFactory.CreateClient(nameof(MappedRestApiDestinationWriter));
        foreach (var record in records)
        {
            await MappedDestinationSerialization.PostJsonAsync(httpClient, endpoint, record, cancellationToken);
        }

        return records.Count;
    }
}
