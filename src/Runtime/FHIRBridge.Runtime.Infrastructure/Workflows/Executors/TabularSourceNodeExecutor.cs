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
/// <para>A node that names its resource types (<see cref="TabularSourceSettings.StreamsKey"/>) reads each type from its
/// own query or file through its own template, and the results become one batch. An older node reads one query or
/// file through all its templates (<see cref="TabularSourceSettings.TemplatesKey"/>), unchanged.</para>
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

        var maxRows = TabularSourceSettings.ClampMaxRows(ReadStringConfiguration(node, TabularSourceSettings.MaxRowsKey));
        var kind = ReadStringConfiguration(node, TabularSourceSettings.KindKey)?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ReadStringConfiguration(node, TabularSourceSettings.StreamsKey)))
        {
            return await ExecuteStreamsAsync(node, kind, maxRows, cancellationToken);
        }

        var templates = TabularFhirTemplateEngine.ParseTemplates(ReadStringConfiguration(node, TabularSourceSettings.TemplatesKey));

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
        return Output(node, kind, built, table.Rows.Count, table.Truncated, []);
    }

    private async Task<WorkflowNodeOutput> ExecuteStreamsAsync(
        WorkflowNode node, string? kind, int maxRows, CancellationToken cancellationToken)
    {
        if (kind is not (TabularSourceSettings.CsvKind or TabularSourceSettings.SqlKind))
        {
            throw new InvalidOperationException("The CSV / SQL Table source must read CSV files or SQL queries.");
        }

        var streams = TabularStreams.Parse(ReadStringConfiguration(node, TabularSourceSettings.StreamsKey), kind);
        var engine = kind == TabularSourceSettings.SqlKind
            ? TabularSourceService.NormalizeEngine(ReadStringConfiguration(node, TabularSourceSettings.SqlEngineKey))
            : null;
        var secret = new SecretReference(
            ReadStringConfiguration(node, TabularSourceSettings.SecretKeyVaultNameKey) ?? string.Empty,
            ReadStringConfiguration(node, TabularSourceSettings.SecretNameKey) ?? string.Empty);

        // Several types may read the same file: it is decrypted and parsed once per run.
        var files = new Dictionary<Guid, TabularRows>();
        var resources = new List<TabularRenderedResource>();
        var errors = new List<string>();
        var perType = new List<Dictionary<string, object?>>();
        int errorCount = 0, duplicates = 0, rowsRead = 0;
        var truncated = false;
        foreach (var stream in streams)
        {
            TabularRows rows;
            if (engine is not null)
            {
                rows = await _reader!.ReadSqlAsync(new TabularSqlQuery(engine, secret, stream.Query!), maxRows, cancellationToken);
            }
            else
            {
                if (!files.TryGetValue(stream.FileId!.Value, out var file))
                {
                    file = await _reader!.ReadFileAsync(stream.FileId.Value, TabularSourceSettings.MaxAllowedRows, cancellationToken);
                    files[stream.FileId.Value] = file;
                }

                rows = TabularStreams.ApplyRowFilter(file, stream);
                if (rows.Rows.Count > maxRows)
                {
                    rows = rows with { Rows = rows.Rows.Take(maxRows).ToList(), Truncated = true };
                }
            }

            var built = TabularResourceBuilder.Build(rows, [stream.Template]);
            resources.AddRange(built.Resources);
            errors.AddRange(built.Errors.Select(e => $"{stream.ResourceType}: {e}"));
            errorCount += built.ErrorCount;
            duplicates += built.DuplicatesDropped;
            rowsRead += rows.Rows.Count;
            truncated |= rows.Truncated;
            perType.Add(new Dictionary<string, object?>
            {
                ["resourceType"] = stream.ResourceType,
                ["rowsRead"] = rows.Rows.Count,
                ["rowsTruncated"] = rows.Truncated,
                ["built"] = built.Resources.Count,
                ["rowErrorCount"] = built.ErrorCount,
            });
        }

        // The same resource from two entries (a patient read by two queries) is kept once, as within one entry.
        var seen = new HashSet<(string Type, string Id)>();
        var kept = new List<TabularRenderedResource>();
        foreach (var resource in resources)
        {
            if (resource.ResourceId is { } id && !seen.Add((resource.ResourceType, id)))
            {
                duplicates++;
                continue;
            }

            kept.Add(resource);
        }

        var combined = new TabularBuildResult(kept, errors.Take(TabularResourceBuilder.MaxReportedErrors).ToList(), errorCount, duplicates);
        return Output(node, kind, combined, rowsRead, truncated, perType);
    }

    private WorkflowNodeOutput Output(
        WorkflowNode node, string? kind, TabularBuildResult built, int rowsRead, bool truncated, IReadOnlyList<Dictionary<string, object?>> perType)
    {
        var envelopes = built.Resources
            .Select(r => new ResourceEnvelope(r.ResourceType, r.ResourceId ?? string.Empty, r.Json))
            .ToList();

        var metadata = new Dictionary<string, object?>
        {
            ["executor"] = nameof(TabularSourceNodeExecutor),
            ["kind"] = kind,
            ["count"] = envelopes.Count,
            ["rowsRead"] = rowsRead,
            ["rowsTruncated"] = truncated,
            ["resourceTypeCounts"] = envelopes.GroupBy(e => e.ResourceType, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            ["duplicatesDropped"] = built.DuplicatesDropped,
            ["rowErrorCount"] = built.ErrorCount,
            ["rowErrors"] = built.Errors,
            ["resourceTypes"] = perType,
        };

        Logger.LogInformation(
            "Tabular source read {RowCount} rows ({Kind}, truncated {Truncated}) and built {ResourceCount} resources; {ErrorCount} row errors, {Duplicates} duplicates dropped.",
            rowsRead, kind, truncated, envelopes.Count, built.ErrorCount, built.DuplicatesDropped);

        return new WorkflowNodeOutput(node.Id, node.NodeType, new ResourceBatch(envelopes), WorkflowDataContract.ResourceBatch, metadata);
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new ResourceBatch([]);
}
