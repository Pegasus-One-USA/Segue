using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

public sealed class MappedSnowflakeDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedSnowflakeDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
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
        if (records.Count == 0)
        {
            return new DestinationWriteResult(0);
        }

        var target = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var httpClient = _httpClientFactory.CreateClient(nameof(MappedSnowflakeDestinationWriter));

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            foreach (var record in records)
            {
                await MappedDestinationSerialization.PostJsonAsync(httpClient, target, record, cancellationToken);
            }

            return new DestinationWriteResult(records.Count);
        }

        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "snowflake.ndjson");
        await MappedDestinationSerialization.WriteTextTargetAsync(
            target,
            fileName,
            MappedDestinationSerialization.ToNdjson(records),
            httpClient,
            cancellationToken);

        return new DestinationWriteResult(records.Count);
    }
}
