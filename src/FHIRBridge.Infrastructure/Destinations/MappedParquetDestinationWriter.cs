using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records as an Apache Parquet file (columnar). Every column is written as a nullable string for
/// portability — Parquet readers (Spark, Snowflake, Synapse, Databricks) can cast on read. The destination secret
/// holds either an output directory/path or a pre-signed blob/object PUT URL (e.g. Azure Blob / S3) for direct upload.
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

        var target = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var columns = MappedDestinationSerialization.GetColumns(records);

        var dataFields = columns.Select(column => new DataField<string>(column)).ToArray();
        var schema = new ParquetSchema(dataFields.Cast<Field>().ToArray());
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "parquet");

        using var buffer = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, buffer, cancellationToken: cancellationToken))
        using (var rowGroup = writer.CreateRowGroup())
        {
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];
                var cells = records.Select(record => MappedDestinationSerialization.GetCell(record, column)).ToArray();
                await rowGroup.WriteColumnAsync(new DataColumn(dataFields[columnIndex], cells), cancellationToken);
            }
        }

        await MappedDestinationSerialization.WriteBinaryTargetAsync(
            target, fileName, buffer.ToArray(),
            _httpClientFactory.CreateClient(nameof(MappedParquetDestinationWriter)),
            cancellationToken);

        return records.Count;
    }
}
