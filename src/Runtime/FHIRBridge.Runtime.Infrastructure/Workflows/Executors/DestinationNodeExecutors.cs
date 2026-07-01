using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public sealed class SqlServerDestinationNodeExecutor : DestinationNodeExecutor
{
    public SqlServerDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.SqlServerDestination, DestinationType.SqlServer, writerFactory)
    {
    }
}

public sealed class AzureSqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public AzureSqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.AzureSqlDestination, DestinationType.AzureSql, writerFactory)
    {
    }
}

public sealed class BlobDestinationNodeExecutor : DestinationNodeExecutor
{
    public BlobDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.BlobDestination, DestinationType.BlobStorage, writerFactory)
    {
    }
}

public sealed class PowerBiDestinationNodeExecutor : DestinationNodeExecutor
{
    public PowerBiDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.PowerBiDestination, DestinationType.PowerBi, writerFactory)
    {
    }
}

public sealed class PostgreSqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public PostgreSqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.PostgreSqlDestination, DestinationType.PostgreSql, writerFactory)
    {
    }
}

public sealed class MySqlDestinationNodeExecutor : DestinationNodeExecutor
{
    public MySqlDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.MySqlDestination, DestinationType.MySql, writerFactory)
    {
    }
}

public sealed class SnowflakeDestinationNodeExecutor : DestinationNodeExecutor
{
    public SnowflakeDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.SnowflakeDestination, DestinationType.Snowflake, writerFactory)
    {
    }
}

public sealed class TableauDestinationNodeExecutor : DestinationNodeExecutor
{
    public TableauDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.TableauDestination, DestinationType.Tableau, writerFactory)
    {
    }
}

public sealed class DatabricksDestinationNodeExecutor : DestinationNodeExecutor
{
    public DatabricksDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.DatabricksDestination, DestinationType.Databricks, writerFactory)
    {
    }
}

public sealed class S3DestinationNodeExecutor : DestinationNodeExecutor
{
    public S3DestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.S3Destination, DestinationType.S3, writerFactory)
    {
    }
}

public sealed class FhirRepositoryDestinationNodeExecutor : DestinationNodeExecutor
{
    public FhirRepositoryDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.FhirRepositoryDestination, DestinationType.FhirRepository, writerFactory)
    {
    }
}

public sealed class CsvDestinationNodeExecutor : DestinationNodeExecutor
{
    public CsvDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.CsvDestination, DestinationType.Csv, writerFactory)
    {
    }
}

public sealed class ExcelDestinationNodeExecutor : DestinationNodeExecutor
{
    public ExcelDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.ExcelDestination, DestinationType.Excel, writerFactory)
    {
    }
}

public sealed class NdjsonDestinationNodeExecutor : DestinationNodeExecutor
{
    public NdjsonDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.NdjsonDestination, DestinationType.Ndjson, writerFactory)
    {
    }
}

public sealed class ParquetDestinationNodeExecutor : DestinationNodeExecutor
{
    public ParquetDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.ParquetDestination, DestinationType.Parquet, writerFactory)
    {
    }
}

public sealed class AvroDestinationNodeExecutor : DestinationNodeExecutor
{
    public AvroDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.AvroDestination, DestinationType.Avro, writerFactory)
    {
    }
}

public sealed class ProtobufDestinationNodeExecutor : DestinationNodeExecutor
{
    public ProtobufDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.ProtobufDestination, DestinationType.Protobuf, writerFactory)
    {
    }
}

public sealed class PdfDestinationNodeExecutor : DestinationNodeExecutor
{
    public PdfDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.PdfDestination, DestinationType.Pdf, writerFactory)
    {
    }
}

public sealed class SftpDestinationNodeExecutor : DestinationNodeExecutor
{
    public SftpDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.SftpDestination, DestinationType.Sftp, writerFactory)
    {
    }
}

public sealed class RestApiDestinationNodeExecutor : DestinationNodeExecutor
{
    public RestApiDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.RestApiDestination, DestinationType.RestApi, writerFactory)
    {
    }
}

public sealed class InMemoryDestinationNodeExecutor : DestinationNodeExecutor
{
    public InMemoryDestinationNodeExecutor(IConfiguredDestinationWriterFactory? writerFactory = null)
        : base(WorkflowNodeTypes.InMemoryDestination, DestinationType.InMemory, writerFactory)
    {
    }
}

public abstract class DestinationNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly DestinationType _destinationType;
    private readonly IConfiguredDestinationWriterFactory? _writerFactory;

    protected DestinationNodeExecutor(
        string nodeType,
        DestinationType destinationType,
        IConfiguredDestinationWriterFactory? writerFactory)
        : base(nodeType, WorkflowDataContract.DestinationWriteResult)
    {
        _destinationType = destinationType;
        _writerFactory = writerFactory;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var records = PassThroughNodeExecutor.ReadMappedRecords(inputs).ToArray();
        var destination = ReadConfiguration<DestinationConfiguration>(node, "destination")
            ?? CreateDestinationConfiguration(context, node);
        var mappingProfile = ReadConfiguration<MappingProfile>(node, "mappingProfile")
            ?? CreateMappingProfile(context, node, records);

        if (_writerFactory is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        var writer = _writerFactory.Create(_destinationType);
        var written = await writer.WriteAsync(destination, mappingProfile, records, cancellationToken);
        var result = new DestinationWriteResult(destination.Id.ToString("N"), written, DateTimeOffset.UtcNow);

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            result,
            WorkflowDataContract.DestinationWriteResult,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["destinationType"] = _destinationType.ToString(),
                ["recordsWritten"] = written
            });
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
    {
        var destinationId = node.Configuration.FirstOrDefault(configuration =>
                string.Equals(configuration.Key, "destinationId", StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?? node.Id.ToString("N");

        return new DestinationWriteResult(destinationId, inputs.Count, DateTimeOffset.UtcNow);
    }

    private DestinationConfiguration CreateDestinationConfiguration(WorkflowExecutionContext context, WorkflowNode node)
    {
        var target = ReadStringConfiguration(node, "target");
        return new DestinationConfiguration(
            context.TenantId,
            node.DisplayName,
            _destinationType,
            new SecretReference(string.Empty, string.Empty),
            target);
    }

    private static MappingProfile CreateMappingProfile(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var resourceType = ReadStringConfiguration(node, "resourceType")
            ?? records.FirstOrDefault()?.ResourceType
            ?? "Patient";
        var destinationObject = ReadStringConfiguration(node, "destinationObject") ?? resourceType;

        return new MappingProfile(
            context.TenantId,
            node.DisplayName,
            resourceType,
            Guid.Empty,
            Guid.Empty,
            destinationObject,
            []);
    }
}
