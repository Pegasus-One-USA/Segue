using FHIRBridge.Application.DTOs;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Serializes mapped records to an Apache Parquet file in memory. Extracted from
/// <see cref="MappedParquetDestinationWriter"/> when the Fabric/OneLake destination needed the identical columnar
/// encoding — both now share one implementation rather than keeping two copies of the schema-and-row-group dance
/// in step by hand.
///
/// Every column is written as a nullable string for portability: the mapped values reaching a destination are
/// already stringly-typed (see <see cref="MappedDestinationSerialization.GetCell"/>), and every Parquet reader that
/// matters — Spark, Fabric, Snowflake, Databricks, DuckDB — casts on read.
/// </summary>
internal static class MappedDestinationParquetSerializer
{
    public static Task<byte[]> SerializeAsync(
        IReadOnlyCollection<MappedDestinationRecord> records, CancellationToken cancellationToken)
        => SerializeAsync(records, MappedDestinationSerialization.GetColumns(records), cancellationToken);

    /// <summary>
    /// Writes exactly the columns given, in order. The Warehouse load needs this: its COPY INTO names the target
    /// table's columns, so the staged file must carry those and only those — the lineage columns
    /// <see cref="MappedDestinationSerialization.GetColumns"/> prepends do not exist in a customer-owned table,
    /// and COPY INTO rejects the file rather than ignoring them.
    /// </summary>
    public static async Task<byte[]> SerializeAsync(
        IReadOnlyCollection<MappedDestinationRecord> records,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        var dataFields = columns.Select(column => new DataField<string>(column)).ToArray();
        var schema = new ParquetSchema(dataFields.Cast<Field>().ToArray());

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

        return buffer.ToArray();
    }
}
