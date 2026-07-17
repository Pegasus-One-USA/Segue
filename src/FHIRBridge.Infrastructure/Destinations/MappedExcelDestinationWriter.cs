using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

public sealed class MappedExcelDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedExcelDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
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

        var targetRootOrUrl = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "csv");
        await MappedDestinationSerialization.WriteTextTargetAsync(
            targetRootOrUrl,
            fileName,
            MappedDestinationSerialization.ToCsv(records),
            _httpClientFactory.CreateClient(nameof(MappedExcelDestinationWriter)),
            cancellationToken);

        return new DestinationWriteResult(records.Count);
    }
}
