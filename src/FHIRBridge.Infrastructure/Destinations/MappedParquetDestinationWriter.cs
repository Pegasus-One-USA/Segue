using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records as an Apache Parquet file (columnar), encoded by the shared
/// <see cref="MappedDestinationParquetSerializer"/> (which the Fabric/OneLake destination also uses). The
/// destination secret holds either an output directory/path or a pre-signed blob/object PUT URL (e.g. Azure Blob /
/// S3) for direct upload.
/// </summary>
public sealed class MappedParquetDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedParquetDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
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
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "parquet");
        var payload = await MappedDestinationParquetSerializer.SerializeAsync(records, cancellationToken);

        await MappedDestinationSerialization.WriteBinaryTargetAsync(
            target, fileName, payload,
            _httpClientFactory.CreateClient(nameof(MappedParquetDestinationWriter)),
            cancellationToken);

        return new DestinationWriteResult(records.Count);
    }
}
