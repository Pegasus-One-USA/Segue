using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// First-class writer for the <c>Csv</c> destination. CSV formerly piggy-backed on
/// <see cref="MappedExcelDestinationWriter"/>; it now has its own writer so the two formats can diverge (Excel
/// gaining a real workbook serializer) without one regressing the other. Emits a delimited text file to the
/// resolved local root or upload URL.
/// </summary>
public sealed class MappedCsvDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedCsvDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
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
        if (records.Count == 0)
        {
            return 0;
        }

        var targetRootOrUrl = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "csv");
        await MappedDestinationSerialization.WriteTextTargetAsync(
            targetRootOrUrl,
            fileName,
            MappedDestinationSerialization.ToCsv(records),
            _httpClientFactory.CreateClient(nameof(MappedCsvDestinationWriter)),
            cancellationToken);

        return records.Count;
    }
}
