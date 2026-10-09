using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

/// <summary>
/// The Tabular source node: reads rows from an uploaded CSV or a SQL query (<see cref="ITabularRowReader"/>) and
/// builds FHIR resources from the node's templates (<see cref="TabularFhirTemplateEngine"/>). Its output is an
/// ordinary <see cref="ResourceBatch"/>, so every node that takes FHIR resources (transforms, FHIR destinations, EHR
/// write-back) takes it unchanged.
///
/// <para>Fails closed on configuration (no file, no query, broken templates): a source that silently produced
/// nothing would look like an empty table. Rows that build nothing are counted in <c>rowErrors</c> by row number
/// and column, never by value.</para>
/// </summary>
public sealed class TabularSourceNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly ITabularRowReader? _reader;

    public TabularSourceNodeExecutor(ITabularRowReader? reader = null, ILoggerFactory? loggerFactory = null)
        : base(WorkflowNodeTypes.TabularSource, WorkflowDataContract.ResourceBatch, loggerFactory)
    {
        _reader = reader;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            throw new InvalidOperationException("The CSV / SQL Table source is not wired to a row reader.");
        }

        if (TabularSourceSettings.NormalizeDatasetKey(ReadStringConfiguration(node, TabularSourceSettings.DatasetKeyKey)) is null)
        {
            throw new InvalidOperationException(
                "The CSV / SQL Table source needs a dataset key (3 to 64 letters, digits or hyphens) that names this data set.");
        }

        var templates = TabularFhirTemplateEngine.ParseTemplates(ReadStringConfiguration(node, TabularSourceSettings.TemplatesKey));
        var maxRows = TabularSourceSettings.ClampMaxRows(ReadStringConfiguration(node, TabularSourceSettings.MaxRowsKey));
        var kind = ReadStringConfiguration(node, TabularSourceSettings.KindKey)?.Trim().ToLowerInvariant();

        TabularRows table;
        if (kind == TabularSourceSettings.CsvKind)
        {
            if (!Guid.TryParse(ReadStringConfiguration(node, TabularSourceSettings.FileIdKey), out var fileId) || fileId == Guid.Empty)
            {
                throw new InvalidOperationException("The CSV / SQL Table source has no uploaded CSV file.");
            }

            table = await _reader.ReadFileAsync(fileId, maxRows, cancellationToken);
        }
        else if (kind == TabularSourceSettings.SqlKind)
        {
            table = await _reader.ReadSqlAsync(
                new TabularSqlQuery(
                    TabularSourceService.NormalizeEngine(ReadStringConfiguration(node, TabularSourceSettings.SqlEngineKey)),
                    new SecretReference(
                        ReadStringConfiguration(node, TabularSourceSettings.SecretKeyVaultNameKey) ?? string.Empty,
                        ReadStringConfiguration(node, TabularSourceSettings.SecretNameKey) ?? string.Empty),
                    ReadStringConfiguration(node, TabularSourceSettings.QueryKey) ?? string.Empty),
                maxRows,
                cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("The CSV / SQL Table source must read a CSV file or a SQL query.");
        }

        var built = TabularResourceBuilder.Build(table, templates);
        var envelopes = built.Resources
            .Select(r => new ResourceEnvelope(r.ResourceType, r.ResourceId ?? string.Empty, r.Json))
            .ToList();

        var metadata = new Dictionary<string, object?>
        {
            ["executor"] = nameof(TabularSourceNodeExecutor),
            ["kind"] = kind,
            ["count"] = envelopes.Count,
            ["rowsRead"] = table.Rows.Count,
            ["rowsTruncated"] = table.Truncated,
            ["resourceTypeCounts"] = envelopes.GroupBy(e => e.ResourceType, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            ["duplicatesDropped"] = built.DuplicatesDropped,
            ["rowErrorCount"] = built.ErrorCount,
            ["rowErrors"] = built.Errors,
        };

        Logger.LogInformation(
            "Tabular source read {RowCount} rows ({Kind}, truncated {Truncated}) and built {ResourceCount} resources; {ErrorCount} row errors, {Duplicates} duplicates dropped.",
            table.Rows.Count, kind, table.Truncated, envelopes.Count, built.ErrorCount, built.DuplicatesDropped);

        return new WorkflowNodeOutput(node.Id, node.NodeType, new ResourceBatch(envelopes), WorkflowDataContract.ResourceBatch, metadata);
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new ResourceBatch([]);
}
