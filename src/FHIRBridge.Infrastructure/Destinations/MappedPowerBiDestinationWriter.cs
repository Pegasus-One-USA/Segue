using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

public sealed class MappedPowerBiDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedPowerBiDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var endpointOrFolder = destination.Target ?? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var httpClient = _httpClientFactory.CreateClient(nameof(MappedPowerBiDestinationWriter));

        if (Uri.TryCreate(endpointOrFolder, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            foreach (var record in records)
            {
                await MappedDestinationSerialization.PostJsonAsync(httpClient, endpointOrFolder, record, cancellationToken);
            }

            return new DestinationWriteResult(records.Count);
        }

        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "powerbi.ndjson");
        await MappedDestinationSerialization.WriteTextTargetAsync(
            endpointOrFolder,
            fileName,
            MappedDestinationSerialization.ToNdjson(records),
            httpClient,
            cancellationToken);

        return new DestinationWriteResult(records.Count);
    }
}
