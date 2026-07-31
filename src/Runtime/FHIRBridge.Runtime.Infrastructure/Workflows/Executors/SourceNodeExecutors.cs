using FHIRBridge.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Application.Workflows.Storage;
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
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(WorkflowNodeTypes.EpicSource, RuntimeSourceType.Epic, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore)
    {
    }
}

public sealed class CernerSourceNodeExecutor : SourceNodeExecutor
{
    public CernerSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(WorkflowNodeTypes.CernerSource, RuntimeSourceType.Cerner, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore)
    {
    }
}

public sealed class EClinicalWorksSourceNodeExecutor : SourceNodeExecutor
{
    public EClinicalWorksSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(WorkflowNodeTypes.EClinicalWorksSource, RuntimeSourceType.Healow, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore)
    {
    }
}

public sealed class AthenahealthSourceNodeExecutor : SourceNodeExecutor
{
    public AthenahealthSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(WorkflowNodeTypes.AthenahealthSource, RuntimeSourceType.GenericFhir, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore)
    {
    }
}

public sealed class AllscriptsSourceNodeExecutor : SourceNodeExecutor
{
    public AllscriptsSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(WorkflowNodeTypes.AllscriptsSource, RuntimeSourceType.Allscripts, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore)
    {
    }
}

public sealed class MeditechSourceNodeExecutor : SourceNodeExecutor
{
    public MeditechSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(WorkflowNodeTypes.MeditechSource, RuntimeSourceType.MeditechGreenfield, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore)
    {
    }
}

public sealed class GenericFhirSourceNodeExecutor : SourceNodeExecutor
{
    public GenericFhirSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(WorkflowNodeTypes.GenericFhirSource, RuntimeSourceType.GenericFhir, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore)
    {
    }
}

public sealed class SampleSourceNodeExecutor : SourceNodeExecutor
{
    public SampleSourceNodeExecutor(
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(WorkflowNodeTypes.SampleSource, RuntimeSourceType.Sample, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore)
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
    private readonly IWorkflowDefinitionStore? _workflowDefinitionStore;

    protected SourceNodeExecutor(
        string nodeType,
        RuntimeSourceType sourceType,
        IFhirSourceClientFactory? sourceClientFactory,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null)
        : base(nodeType, WorkflowDataContract.ResourceBatch)
    {
        _sourceType = sourceType;
        _sourceClientFactory = sourceClientFactory;
        _sourceResolver = sourceResolver;
        _syncCursorStore = syncCursorStore;
        _bulkExportClient = bulkExportClient;
        _workflowDefinitionStore = workflowDefinitionStore;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var configuredResourceType = ReadStringConfiguration(node, "resourceType");
        // "searchParameters" is the canonical key for a hand-authored/route-projected node config; the Epic wizard
        // (epic-audience-form.component.ts save(), Search REST's "Search Criteria" field) instead writes its own
        // human-readable field bag under "Search criteria" — without this fallback, anything typed into that field
        // was silently never read, so a wizard-configured Search Criteria had zero effect on the outbound request.
        var searchParameters = ReadStringConfiguration(node, "searchParameters")
            ?? ReadStringConfiguration(node, "Search criteria");

        // The wizard-authored, human-readable "Resources" field (e.g. "Patient, Observation, Condition") is this
        // node's own, per-workflow declaration of what it fetches — two workflows can share one SourceConnection
        // (and so one set of granted scopes) while each still fetching a different, deliberately narrower or wider
        // subset. Takes priority over anything resolved from the connection below, since that's connection-wide and
        // can't express a per-workflow difference the way this node-level field can.
        var configuredResources = ParseCommaSeparatedResourceTypes(ReadStringConfiguration(node, "Resources"));

        // Option A: prefer a real SourceConnection referenced by id (base URL + auth + token resolved live at run time).
        FhirSourceConfiguration? source = null;
        var sourceConnectionId = ReadStringConfiguration(node, "sourceConnectionId");
        if (_sourceResolver is not null && Guid.TryParse(sourceConnectionId, out var connectionId))
        {
            source = await _sourceResolver.ResolveAsync(
                connectionId,
                searchParameters,
                context.TargetPatientId,
                cancellationToken,
                context.PatientSearchCriteria,
                context.CallerId);
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

        // Below configuredResources in priority: a Backend System Search REST retrieval config carries its own
        // resource-type list on the connection itself (potentially several types under one connection with no
        // per-node "Resources" field at all — the route→graph projection path) — an explicit admin choice there, so
        // it wins over the scope-derived guess when set. Otherwise derive the list from the connection's own granted
        // SMART scopes (e.g. "patient/Observation.rs" -> "Observation") rather than silently defaulting to a single
        // resource type: the scopes are the authoritative record of what this connection is actually authorized to
        // fetch, so deriving from them can't drift out of sync the way a separately hand-maintained resource-type
        // list can. Falls back to the single node-config resourceType only when nothing above has anything to say
        // (e.g. a non-interactive/no-scope source) — unchanged behavior for the route→graph projection and any
        // hand-authored node config. If even that is absent, there is no way to know what this node should fetch —
        // silently defaulting to "Patient" here previously meant a misconfigured node would quietly under-fetch
        // instead of failing the run, so this now fails loudly and tells the caller what to configure.
        var resourceTypes = configuredResources is { Count: > 0 }
            ? configuredResources
            : source.ResourceTypes is { Count: > 0 } configured
                ? configured
                : DeriveResourceTypesFromScopes(source.Scopes) is { Count: > 0 } fromScopes
                    ? fromScopes
                    : !string.IsNullOrWhiteSpace(configuredResourceType)
                        ? [configuredResourceType]
                        : throw new InvalidOperationException(
                            $"Source node '{node.Id}' ({node.NodeType}) has no resolvable FHIR resource type: " +
                            "no 'Resources'/'resourceType' node configuration, no connection-level ResourceTypes, " +
                            "and no SMART scopes to derive one from. Configure at least one resource type for this node.");

        // Narrow to whatever this node's downstream destination(s) actually selected — a destination wizard's own
        // "dest_resources" picker is the real record of what's ever written anywhere; without this, a source
        // configured (or scope-derived) for a broader set than any destination consumes silently over-fetches
        // (and, upstream of here, over-requests OAuth scopes for) resource types nobody ever asked for. Only applies
        // a constraint when at least one reachable destination exists — a destination-less run (e.g. a caller that
        // reads this node's raw output directly, with no destination node at all) has nothing to narrow against and
        // keeps fetching exactly what was resolved above, unchanged.
        resourceTypes = await RestrictToDestinationResourceTypesAsync(resourceTypes, node, cancellationToken);

        // A source configured for bulk export ($export) pulls each resource type via the Bulk Data flow instead of a
        // paged search — same downstream envelope projection, so the rest of the DAG is identical. Every other source
        // (and any bulk-configured source when no bulk client is wired) keeps using search — unchanged behavior.
        var useBulkExport = string.Equals(source.RetrievalMethod, "bulk-export", StringComparison.OrdinalIgnoreCase)
            && _bulkExportClient is not null;

        // Extract "Patient" first (regardless of where it falls in the wizard-authored order) so its resulting ids
        // become a cohort every sibling resource type is scoped to below — without this, a multi-resource selection
        // (e.g. Patient + Observation) would fetch Observation completely unscoped against the whole tenant.
        var executionOrder = resourceTypes
            .OrderBy(type => string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        // An explicitly configured System/Group export always resolves to the SAME request shape (GroupId, Since,
        // OutputFormat) for every resource type — see BuildBulkExportRequestAsync's explicitlyUnscopable branch,
        // which a System/Group scope always takes regardless of any Patient cohort. Requesting each type in its own
        // job (one _type=Patient-only job, one _type=Observation-only job, ...) trips a real Epic Interconnect
        // Group-export limitation: a job scoped to just _type=Patient makes Epic fall back to an unscoped/
        // demographics Patient search internally, which it rejects ("requires demographics or _id parameter",
        // business-rule 59159). Requesting every type together in one job — the FHIR Bulk Data spec's intended
        // usage — avoids that job entirely, so this is fetched once up front instead of once per type below.
        var explicitSystemOrGroupExport = useBulkExport
            && !string.IsNullOrWhiteSpace(source.ExportScope)
            && BulkExportScopes.Parse(source.ExportScope) is BulkExportScope.System or BulkExportScope.Group;

        IReadOnlyDictionary<string, IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>>? batchedBulkResourcesByType = null;
        if (explicitSystemOrGroupExport)
        {
            var batchedRequest = BuildBatchedBulkExportRequest(source, executionOrder);
            var batchedResources = await _bulkExportClient!.ExportAsync(batchedRequest, source, cancellationToken);
            batchedBulkResourcesByType = batchedResources
                .GroupBy(resource => resource.ResourceType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> (group) => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlyList<string>? cohortPatientIds = null;
        var resources = new List<ResourceEnvelope>();
        var skippedResourceTypes = new List<string>();
        foreach (var type in executionOrder)
        {
            var isPatientType = string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> page;
            try
            {
                page = useBulkExport
                    ? batchedBulkResourcesByType is not null
                        ? batchedBulkResourcesByType.TryGetValue(type, out var batchedPage) ? batchedPage : []
                        : await _bulkExportClient!.ExportAsync(
                            await BuildBulkExportRequestAsync(
                                source, type, isPatientType ? null : cohortPatientIds, context.WorkflowRunId, cancellationToken),
                            source,
                            cancellationToken)
                    : isPatientType || cohortPatientIds is not { Count: > 0 }
                        ? await SearchWithPolicyAsync(client, type, source, context.WorkflowRunId, cancellationToken)
                        : await SearchCohortScopedAsync(client, type, source, cohortPatientIds, context.WorkflowRunId, cancellationToken);
            }
            catch (FHIRBridge.Runtime.Domain.Exceptions.ResourceAuthorizationException authorizationException)
            {
                // Patient (or whichever type seeds the cohort) is the parent every sibling resource type here is
                // scoped off — if it isn't authorized, there is no partial result to isolate: cancel the whole run
                // rather than silently running the other types unscoped or not at all. A non-parent type failing
                // the same way just means less data, not an unrunnable workflow, so it's skipped and the rest of
                // the node's fetch continues.
                if (isPatientType)
                {
                    throw new FHIRBridge.Runtime.Domain.Exceptions.WorkflowRunCancelledException(
                        type, authorizationException.Message, authorizationException);
                }

                skippedResourceTypes.Add(
                    $"{type}: not authorized for this app ({authorizationException.StatusCode}) — {authorizationException.Message}");
                continue;
            }

            resources.AddRange(page.Select(resource => new ResourceEnvelope(
                resource.ResourceType,
                resource.ResourceId ?? string.Empty,
                resource.RawJson)));

            if (isPatientType)
            {
                cohortPatientIds = page
                    .Select(resource => resource.ResourceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .Distinct()
                    .ToList();
            }
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
                ["count"] = resources.Count,
                // Reflects what actually ran (bulk client available and configured), not just what was configured —
                // lets a caller (e.g. the /run endpoint's Activity Feed summary) label a run as a Bulk Export
                // without duplicating this resolution logic.
                ["retrievalMethod"] = useBulkExport ? "bulk-export" : "search-rest",
                // Non-null only when "Patient" was among this node's resource types — how many patients its own
                // extraction found, and so how many sibling resource types (Observation, Condition, ...) got scoped
                // to. Absent/zero means every other resource type in this node ran unscoped (no Patient selected).
                ["cohortSize"] = cohortPatientIds?.Count,
                // Non-parent resource types this node selected but couldn't fetch because the app isn't authorized
                // for them — the orchestrator surfaces these as a PartialSuccess rather than silently dropping them.
                ["skippedResourceTypes"] = skippedResourceTypes.Count > 0 ? skippedResourceTypes.ToArray() : null
            });
    }

    /// <summary>
    /// Batches a cohort-scoped search: <see cref="FhirSourceConfiguration.PatientIds"/> would be OR'd into a single
    /// <c>patient=</c> parameter by <c>FhirSourceConnectorBase.ApplyPatientScopeAsync</c> if more than one id were
    /// passed through at once, but Epic (and US Core generally) rejects a clinical-resource search scoped to more
    /// than one patient outright — "A given request can only apply to one patient" — there is no larger batch size
    /// that's actually safe. So the cohort is split one patient per batch, each run through the existing per-type
    /// retry/timeout wrapper, and the pages concatenated. A single-patient cohort still makes exactly one request,
    /// unchanged.
    /// </summary>
    private const int CohortBatchSize = 1;

    private async Task<IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>> SearchCohortScopedAsync(
        IFhirSourceClient client,
        string resourceType,
        FhirSourceConfiguration source,
        IReadOnlyList<string> cohortPatientIds,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        var results = new List<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>();
        foreach (var batch in cohortPatientIds.Chunk(CohortBatchSize))
        {
            // This node's connection-wide SearchParameters (e.g. an "identifier=<MRN list>" criteria the wizard's
            // "Search criteria" field used to find the Patient cohort in the first place — see the ExecuteAsync
            // comment on the "Search criteria" fallback) identified WHICH patients to fetch; it does not describe a
            // valid filter for any other resource type. Once a patient cohort exists, siblings are scoped purely by
            // patient={id} (plus their own category/status defaults from ApplyDefaultSearchParameters) — carrying
            // the raw criteria forward made every cohort-scoped sibling search additionally (and meaninglessly)
            // filter by the patients' MRNs, which Condition/Observation don't recognize, silently returning zero
            // results instead of the patient's actual data.
            var batchSource = source with { PatientIds = batch, TargetPatientId = null, SearchParameters = null };
            results.AddRange(await SearchWithPolicyAsync(client, resourceType, batchSource, workflowRunId, cancellationToken));
        }

        return results;
    }

    /// <summary>
    /// Parses the wizard-authored, comma-separated "Resources" node config field (e.g.
    /// <c>"Patient, Observation, Condition"</c>) into a trimmed, non-empty resource-type list. Returns an empty list
    /// (not a single-element list of whitespace) for a null/blank field, so callers can cleanly fall through to the
    /// next priority.
    /// </summary>
    private static IReadOnlyList<string> ParseCommaSeparatedResourceTypes(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Narrows <paramref name="resourceTypes"/> down to whatever this node's downstream destination node(s) actually
    /// selected — each destination's own wizard-authored "dest_resources" field, unioned across every destination
    /// reachable from this node in the workflow graph. Prevents the over-fetch (and, further upstream, over-broad
    /// OAuth scope requests) that results when a source is configured/scope-derived for a broader resource-type set
    /// than any destination ever consumes. A run with no destination reachable at all (e.g. a caller that reads this
    /// node's raw output directly, with nothing downstream to narrow against) returns <paramref name="resourceTypes"/>
    /// unchanged — this only ever removes types nothing downstream wants, never adds ones the source itself wasn't
    /// already configured/authorized for.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> RestrictToDestinationResourceTypesAsync(
        IReadOnlyCollection<string> resourceTypes,
        WorkflowNode node,
        CancellationToken cancellationToken)
    {
        if (_workflowDefinitionStore is null)
        {
            return resourceTypes;
        }

        var definition = await _workflowDefinitionStore.GetAsync(node.WorkflowDefinitionId, cancellationToken);
        if (definition is null)
        {
            return resourceTypes;
        }

        var reachable = new HashSet<Guid>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(node.Id);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var edge in definition.Edges.Where(e => e.FromNodeId == current))
            {
                if (reachable.Add(edge.ToNodeId))
                {
                    frontier.Enqueue(edge.ToNodeId);
                }
            }
        }

        var destinationResourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destinationNode in definition.Nodes.Where(
            candidate => reachable.Contains(candidate.Id) && candidate.Category == WorkflowNodeCategory.Destination))
        {
            foreach (var type in ParseCommaSeparatedResourceTypes(ReadStringConfiguration(destinationNode, "dest_resources")))
            {
                destinationResourceTypes.Add(type);
            }
        }

        return destinationResourceTypes.Count == 0
            ? resourceTypes
            : resourceTypes.Where(destinationResourceTypes.Contains).ToList();
    }

    /// <summary>
    /// Extracts the distinct FHIR resource types a set of granted SMART scopes actually covers, in the standard
    /// clinical scope shape <c>{context}/{ResourceType}.{permissions}</c> (SMART v1 <c>patient/Observation.rs</c> or
    /// v2 granular <c>patient/Observation.read</c>; <c>user/</c>/<c>system/</c> contexts too). Non-resource scopes
    /// (<c>openid</c>, <c>fhirUser</c>, <c>offline_access</c>, <c>launch</c>, <c>launch/patient</c>, ...) are skipped
    /// — their segment after the slash isn't a capitalized FHIR resource type name, which every real resource-scope
    /// segment is (<c>Patient</c>, <c>Observation</c>, ...). Order-preserving and de-duplicated so the derived list
    /// is stable across calls for the same scope set.
    /// </summary>
    private static IReadOnlyList<string> DeriveResourceTypesFromScopes(IReadOnlyCollection<string> scopes)
    {
        var resourceTypes = new List<string>();
        foreach (var scope in scopes)
        {
            var slashIndex = scope.IndexOf('/');
            if (slashIndex < 0 || slashIndex == scope.Length - 1)
            {
                continue;
            }

            var afterSlash = scope[(slashIndex + 1)..];
            var dotIndex = afterSlash.IndexOf('.');
            var candidate = dotIndex >= 0 ? afterSlash[..dotIndex] : afterSlash;

            if (candidate.Length > 0
                && char.IsUpper(candidate[0])
                && !resourceTypes.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                resourceTypes.Add(candidate);
            }
        }

        return resourceTypes;
    }

    /// <summary>
    /// Applies the retrieval config's Retry Policy and Timeout around one SearchAsync call — a node-level layer on
    /// top of (not a replacement for) whatever transient-fault retry the connector's own HttpClient already does
    /// internally: this retries the *whole* resource-type fetch if it still fails/times out after those internal
    /// retries are exhausted. A null source.RetryPolicy/TimeoutSeconds (the default for every source that predates
    /// this field) is a single attempt with no per-call timeout — unchanged behavior.
    /// </summary>
    private async Task<IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>> SearchWithPolicyAsync(
        IFhirSourceClient client,
        string resourceType,
        FhirSourceConfiguration source,
        Guid workflowRunId,
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
                var result = await client.SearchAsync(resourceType, source, timeoutCts?.Token ?? cancellationToken);
                return result;
            }
            catch (Exception ex) when (ex is not FHIRBridge.Runtime.Domain.Exceptions.ResourceAuthorizationException
                && attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
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

    // Builds a single $export request covering every resource type at once for an explicitly System/Group-scoped
    // source (see explicitSystemOrGroupExport above) — always PatientIds: null, matching what
    // BuildBulkExportRequestAsync's explicitlyUnscopable branch would return per-type for this scope.
    private static FhirBulkExportRequest BuildBatchedBulkExportRequest(FhirSourceConfiguration source, IReadOnlyList<string> resourceTypes)
    {
        var scope = BulkExportScopes.Parse(source.ExportScope);
        return new FhirBulkExportRequest(
            scope,
            GroupId: scope == BulkExportScope.Group ? source.GroupId : null,
            ResourceTypes: resourceTypes,
            Since: source.Since,
            PatientIds: null,
            OutputFormat: source.OutputFormat);
    }

    // Projects the resolved source's bulk-export settings onto a $export request for one resource type — mirrors the
    // configured-pipeline plane so a graph run and a route run scope the export identically. Scope drives which id
    // narrows the export (Group id vs patient list); System carries neither. When this node's own Patient
    // extraction discovered a cohort (cohortPatientIds), an unset or already-Patient-scoped export is narrowed to
    // it. System/Group scope can't be narrowed to an ad hoc cohort by $export semantics — left as configured.
    private async Task<FhirBulkExportRequest> BuildBulkExportRequestAsync(
        FhirSourceConfiguration source,
        string resourceType,
        IReadOnlyList<string>? cohortPatientIds,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        var configuredScope = BulkExportScopes.Parse(source.ExportScope);
        var hasCohort = cohortPatientIds is { Count: > 0 };

        // An unset ExportScope parses to System (BulkExportScopes.Parse's default), indistinguishable from an
        // explicit "system" — but only an *explicit* System/Group choice should be left un-narrowed below; an
        // unset scope should still pick up the cohort like the Patient-scope branch does.
        var explicitlyUnscopable = hasCohort
            && !string.IsNullOrWhiteSpace(source.ExportScope)
            && configuredScope is BulkExportScope.System or BulkExportScope.Group;

        if (explicitlyUnscopable)
        {
            return new FhirBulkExportRequest(
                configuredScope,
                GroupId: configuredScope == BulkExportScope.Group ? source.GroupId : null,
                ResourceTypes: [resourceType],
                Since: source.Since,
                PatientIds: null,
                OutputFormat: source.OutputFormat);
        }

        var effectiveScope = hasCohort ? BulkExportScope.Patient : configuredScope;
        return new FhirBulkExportRequest(
            effectiveScope,
            GroupId: effectiveScope == BulkExportScope.Group ? source.GroupId : null,
            ResourceTypes: [resourceType],
            Since: source.Since,
            PatientIds: effectiveScope == BulkExportScope.Patient ? (cohortPatientIds ?? source.PatientIds) : null,
            OutputFormat: source.OutputFormat);
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new ResourceBatch([]);
}
