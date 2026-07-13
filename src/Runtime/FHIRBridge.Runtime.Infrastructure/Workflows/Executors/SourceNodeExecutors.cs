using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public sealed class EpicSourceNodeExecutor : SourceNodeExecutor
{
    public EpicSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(WorkflowNodeTypes.EpicSource, RuntimeSourceType.Epic, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient)
    {
    }
}

public sealed class CernerSourceNodeExecutor : SourceNodeExecutor
{
    public CernerSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(WorkflowNodeTypes.CernerSource, RuntimeSourceType.Cerner, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient)
    {
    }
}

public sealed class EClinicalWorksSourceNodeExecutor : SourceNodeExecutor
{
    public EClinicalWorksSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(WorkflowNodeTypes.EClinicalWorksSource, RuntimeSourceType.Healow, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient)
    {
    }
}

public sealed class AthenahealthSourceNodeExecutor : SourceNodeExecutor
{
    public AthenahealthSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(WorkflowNodeTypes.AthenahealthSource, RuntimeSourceType.GenericFhir, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient)
    {
    }
}

public sealed class AllscriptsSourceNodeExecutor : SourceNodeExecutor
{
    public AllscriptsSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(WorkflowNodeTypes.AllscriptsSource, RuntimeSourceType.Allscripts, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient)
    {
    }
}

public sealed class MeditechSourceNodeExecutor : SourceNodeExecutor
{
    public MeditechSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(WorkflowNodeTypes.MeditechSource, RuntimeSourceType.MeditechGreenfield, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient)
    {
    }
}

public sealed class GenericFhirSourceNodeExecutor : SourceNodeExecutor
{
    public GenericFhirSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(WorkflowNodeTypes.GenericFhirSource, RuntimeSourceType.GenericFhir, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient)
    {
    }
}

public sealed class SampleSourceNodeExecutor : SourceNodeExecutor
{
    public SampleSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(WorkflowNodeTypes.SampleSource, RuntimeSourceType.Sample, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient)
    {
    }
}

public sealed class Hl7v2MllpSourceNodeExecutor : WorkflowNodeExecutorBase
{
    public Hl7v2MllpSourceNodeExecutor()
        : base(WorkflowNodeTypes.Hl7v2MllpSource, WorkflowDataContract.ResourceBatch)
    {
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new ResourceBatch([]);
}

public abstract class SourceNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly RuntimeSourceType _sourceType;
    private readonly IFhirSourceClientFactory? _sourceClientFactory;
    private readonly ISourceConnectionRuntimeResolver? _sourceResolver;
    private readonly ISourceConnectionSyncCursorStore? _syncCursorStore;
    private readonly IFhirBulkExportClient? _bulkExportClient;

    protected SourceNodeExecutor(
        string nodeType,
        RuntimeSourceType sourceType,
        IFhirSourceClientFactory? sourceClientFactory,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null)
        : base(nodeType, WorkflowDataContract.ResourceBatch)
    {
        _sourceType = sourceType;
        _sourceClientFactory = sourceClientFactory;
        _sourceResolver = sourceResolver;
        _syncCursorStore = syncCursorStore;
        _bulkExportClient = bulkExportClient;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var resourceType = ReadStringConfiguration(node, "resourceType") ?? "Patient";
        var searchParameters = ReadStringConfiguration(node, "searchParameters");

        // Option A: prefer a real SourceConnection referenced by id (base URL + auth + token resolved live at run time).
        FhirSourceConfiguration? source = null;
        var sourceConnectionId = ReadStringConfiguration(node, "sourceConnectionId");
        if (_sourceResolver is not null && Guid.TryParse(sourceConnectionId, out var connectionId))
        {
            source = await _sourceResolver.ResolveAsync(connectionId, searchParameters, context.TargetPatientId, cancellationToken);
        }

        // Fallback: an inline source configuration embedded in node config (used by the route→graph projection).
        source ??= ReadConfiguration<FhirSourceConfiguration>(node, "source")
            ?? ReadConfiguration<FhirSourceConfiguration>(node);

        if (_sourceClientFactory is null || source is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        if (source.SourceType != _sourceType)
        {
            source = source with { SourceType = _sourceType };
        }

        var client = _sourceClientFactory.Create(_sourceType);

        // A Backend System Search REST retrieval config carries its own resource-type list (potentially several
        // types under one connection); fall back to the single node-config resourceType otherwise — unchanged
        // behavior for the route→graph projection and any hand-authored node config.
        var resourceTypes = source.ResourceTypes is { Count: > 0 } configured ? configured : [resourceType];

        // A source configured for bulk export ($export) pulls each resource type via the Bulk Data flow instead of a
        // paged search — same downstream envelope projection, so the rest of the DAG is identical. Every other source
        // (and any bulk-configured source when no bulk client is wired) keeps using search — unchanged behavior.
        var useBulkExport = string.Equals(source.RetrievalMethod, "bulk-export", StringComparison.OrdinalIgnoreCase)
            && _bulkExportClient is not null;

        var resources = new List<ResourceEnvelope>();
        foreach (var type in resourceTypes)
        {
            IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> page = useBulkExport
                ? await _bulkExportClient!.ExportAsync(BuildBulkExportRequest(source, type), source, cancellationToken)
                : await SearchWithPolicyAsync(client, type, source, cancellationToken);

            resources.AddRange(page.Select(resource => new ResourceEnvelope(
                resource.ResourceType,
                resource.ResourceId ?? string.Empty,
                resource.RawJson)));
        }

        if (source.MaxRecords is { } maxRecords && resources.Count > maxRecords)
        {
            resources.RemoveRange(maxRecords, resources.Count - maxRecords);
        }

        if (source.SourceConnectionId is { } resolvedSourceConnectionId && _syncCursorStore is not null)
        {
            await _syncCursorStore.RecordSuccessfulSyncAsync(resolvedSourceConnectionId, DateTime.UtcNow, cancellationToken);
        }

        var payload = new ResourceBatch(resources.ToArray());

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            payload,
            WorkflowDataContract.ResourceBatch,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["resourceType"] = string.Join(',', resourceTypes),
                ["count"] = resources.Count
            });
    }

    /// <summary>
    /// Applies the retrieval config's Retry Policy and Timeout around one SearchAsync call — a node-level layer on
    /// top of (not a replacement for) whatever transient-fault retry the connector's own HttpClient already does
    /// internally: this retries the *whole* resource-type fetch if it still fails/times out after those internal
    /// retries are exhausted. A null source.RetryPolicy/TimeoutSeconds (the default for every source that predates
    /// this field) is a single attempt with no per-call timeout — unchanged behavior.
    /// </summary>
    private static async Task<IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>> SearchWithPolicyAsync(
        IFhirSourceClient client,
        string resourceType,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var maxAttempts = source.RetryPolicy switch
        {
            "fixed-3" => 3,
            "exponential" => 3,
            _ => 1,
        };

        for (var attempt = 1; ; attempt++)
        {
            using var timeoutCts = source.TimeoutSeconds is { } timeoutSeconds
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds!.Value));

            try
            {
                return await client.SearchAsync(resourceType, source, timeoutCts?.Token ?? cancellationToken);
            }
            catch (Exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // The outer token is still live, so whatever was caught is either a timeout (inner token fired) or a
                // transient failure the connector's own retries didn't recover from — back off and try the whole
                // resource-type fetch again.
                var delay = source.RetryPolicy == "exponential"
                    ? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1))
                    : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    // Projects the resolved source's bulk-export settings onto a $export request for one resource type — mirrors the
    // configured-pipeline plane so a graph run and a route run scope the export identically. Scope drives which id
    // narrows the export (Group id vs patient list); System carries neither.
    private static FhirBulkExportRequest BuildBulkExportRequest(FhirSourceConfiguration source, string resourceType)
    {
        var scope = BulkExportScopes.Parse(source.ExportScope);
        return new FhirBulkExportRequest(
            scope,
            GroupId: scope == BulkExportScope.Group ? source.GroupId : null,
            ResourceTypes: [resourceType],
            Since: source.Since,
            PatientIds: scope == BulkExportScope.Patient ? source.PatientIds : null,
            OutputFormat: source.OutputFormat);
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new ResourceBatch([]);
}
