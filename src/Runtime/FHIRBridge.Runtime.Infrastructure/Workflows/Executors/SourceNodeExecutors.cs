using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Governance;
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
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(WorkflowNodeTypes.EpicSource, RuntimeSourceType.Epic, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore, bulkExportJobRepository, exceptionManager, accessTokenProvider, bulkExportConcurrencyOptions)
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
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(WorkflowNodeTypes.CernerSource, RuntimeSourceType.Cerner, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore, bulkExportJobRepository, exceptionManager, accessTokenProvider, bulkExportConcurrencyOptions)
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
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(WorkflowNodeTypes.EClinicalWorksSource, RuntimeSourceType.Healow, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore, bulkExportJobRepository, exceptionManager, accessTokenProvider, bulkExportConcurrencyOptions)
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
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(WorkflowNodeTypes.AthenahealthSource, RuntimeSourceType.Athenahealth, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore, bulkExportJobRepository, exceptionManager, accessTokenProvider, bulkExportConcurrencyOptions)
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
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(WorkflowNodeTypes.AllscriptsSource, RuntimeSourceType.Allscripts, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore, bulkExportJobRepository, exceptionManager, accessTokenProvider, bulkExportConcurrencyOptions)
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
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(WorkflowNodeTypes.MeditechSource, RuntimeSourceType.MeditechGreenfield, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore, bulkExportJobRepository, exceptionManager, accessTokenProvider, bulkExportConcurrencyOptions)
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
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(WorkflowNodeTypes.GenericFhirSource, RuntimeSourceType.GenericFhir, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore, bulkExportJobRepository, exceptionManager, accessTokenProvider, bulkExportConcurrencyOptions)
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
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(WorkflowNodeTypes.SampleSource, RuntimeSourceType.Sample, sourceClientFactory, sourceResolver, syncCursorStore, bulkExportClient, workflowDefinitionStore, bulkExportJobRepository, exceptionManager, accessTokenProvider, bulkExportConcurrencyOptions)
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
    private readonly FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? _bulkExportJobRepository;
    private readonly IGlobalExceptionManager? _exceptionManager;
    private readonly IFhirAccessTokenProvider? _accessTokenProvider;
    private readonly FHIRBridge.Application.Services.BulkExportConcurrencyOptions _bulkExportConcurrencyOptions;

    protected SourceNodeExecutor(
        string nodeType,
        RuntimeSourceType sourceType,
        IFhirSourceClientFactory? sourceClientFactory,
        ISourceConnectionRuntimeResolver? sourceResolver = null,
        ISourceConnectionSyncCursorStore? syncCursorStore = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        FHIRBridge.Application.Abstractions.Persistence.IBulkExportJobRepository? bulkExportJobRepository = null,
        IGlobalExceptionManager? exceptionManager = null,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        Microsoft.Extensions.Options.IOptions<FHIRBridge.Application.Services.BulkExportConcurrencyOptions>? bulkExportConcurrencyOptions = null)
        : base(nodeType, WorkflowDataContract.ResourceBatch)
    {
        _sourceType = sourceType;
        _sourceClientFactory = sourceClientFactory;
        _sourceResolver = sourceResolver;
        _syncCursorStore = syncCursorStore;
        _bulkExportClient = bulkExportClient;
        _workflowDefinitionStore = workflowDefinitionStore;
        _bulkExportJobRepository = bulkExportJobRepository;
        _exceptionManager = exceptionManager;
        _accessTokenProvider = accessTokenProvider;
        _bulkExportConcurrencyOptions = bulkExportConcurrencyOptions?.Value ?? new FHIRBridge.Application.Services.BulkExportConcurrencyOptions();
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

        // TEMPORARY carve-out: every canvas source node is currently persisted with NodeType "EpicSourceNode"
        // regardless of actual vendor (workflow-graph-mapper.service.ts's transformIdForNode() falls through to
        // 'epic' for every vendor except Sample/GenericFhir — a real frontend bug, not yet fixed). That pins this
        // executor's _sourceType to RuntimeSourceType.Epic even for an athenahealth connection, so the unconditional
        // override below would silently stomp SourceConnectionRuntimeResolver's correctly-resolved Athenahealth type
        // back to Epic right before client selection — sending the request through EpicFhirSourceClient (no
        // ah-practice injection, wrong defaults) instead of AthenahealthFhirSourceClient. Trust the resolver's value
        // for athenahealth specifically until the frontend node-type labeling is fixed; every other vendor keeps the
        // existing override behavior unchanged.
        if (source.SourceType != _sourceType && source.SourceType != RuntimeSourceType.Athenahealth)
        {
            source = source with { SourceType = _sourceType };
        }

        // Bulk-export/retrieval settings (Data Retrieval Method, Export Scope, Group ID, Patient ID list, FHIR
        // output format) are workflow-node-scoped — see epic-audience-form.component.ts's "Retrieval Configuration"
        // panel and its accompanying comment: they deliberately live on the node, not on the reusable SourceConnection
        // entity, since one connection can be referenced by several workflows that each need different retrieval
        // behavior. ResolveAsync above only hydrates auth/base-URL/scopes from the connection entity, so a node whose
        // SourceConnection was never separately given matching Retrieval* column values resolves RetrievalMethod to
        // null — useBulkExport below would then silently evaluate false and this run would fall back to an unscoped
        // search-rest fetch instead of running the bulk export the wizard shows as configured. Mirrors
        // workflow-build-assembler.service.ts's buildRetrieval() field mapping exactly, including the ndjson ->
        // application/fhir+ndjson MIME normalization ($export's _outputFormat expects the registered MIME type, not
        // the wizard's short token). Only overrides when the node actually specifies a retrieval method — a node with
        // none keeps whatever the resolved source already carries (unchanged behavior for every other node shape).
        var nodeRetrievalMethod = ReadStringConfiguration(node, "Retrieval method key");
        if (!string.IsNullOrWhiteSpace(nodeRetrievalMethod))
        {
            var nodeExportScope = ReadStringConfiguration(node, "Export scope");
            var nodeOutputFormatToken = ReadStringConfiguration(node, "FHIR output format");
            var nodePatientIds = (ReadStringConfiguration(node, "Patient ID / list") ?? string.Empty)
                .Split([',', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            source = source with
            {
                RetrievalMethod = nodeRetrievalMethod,
                ExportScope = string.IsNullOrWhiteSpace(nodeExportScope) ? null : nodeExportScope,
                GroupId = string.Equals(nodeExportScope, "group", StringComparison.OrdinalIgnoreCase)
                    ? BulkExportGroupIds.ResolveAthenahealthGroupId(source.SourceType, ReadStringConfiguration(node, "Group ID"), source.PracticeId)
                    : null,
                PatientIds = string.Equals(nodeExportScope, "patient", StringComparison.OrdinalIgnoreCase) && nodePatientIds.Length > 0
                    ? nodePatientIds
                    : null,
                OutputFormat = nodeOutputFormatToken is { Length: > 0 } token && token.StartsWith("ndjson", StringComparison.OrdinalIgnoreCase)
                    ? "application/fhir+ndjson"
                    : null,
            };
        }

        var client = _sourceClientFactory.Create(
            source.SourceType == RuntimeSourceType.Athenahealth ? source.SourceType : _sourceType);

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
        IReadOnlyCollection<string> resourceTypes;
        if (configuredResources is { Count: > 0 })
        {
            resourceTypes = configuredResources;
        }
        else if (source.ResourceTypes is { Count: > 0 } configured)
        {
            resourceTypes = configured;
        }
        else if (DeriveResourceTypesFromScopes(source.Scopes) is { Count: > 0 } fromScopes)
        {
            resourceTypes = fromScopes;
        }
        else if (!string.IsNullOrWhiteSpace(configuredResourceType))
        {
            resourceTypes = [configuredResourceType];
        }
        else
        {
            // Last resort before failing outright: a source with no resource-type config of its own (e.g. Generic
            // FHIR, which has no OAuth scopes to derive from at all) still has a real answer as long as some
            // reachable destination declares its own "dest_resources" — the same field RestrictToDestinationResourceTypesAsync
            // below already reads to *narrow* an existing list. Using it to *seed* one too means the source form's
            // own Resource Type field can be optional rather than mandatory, since the destination wizard's data-group
            // picker already captures the same choice for any workflow that has a destination at all.
            var fromDestination = await GetDestinationResourceTypesAsync(node, cancellationToken);
            resourceTypes = fromDestination.Count > 0
                ? fromDestination
                : throw new InvalidOperationException(
                    $"Source node '{node.Id}' ({node.NodeType}) has no resolvable FHIR resource type: " +
                    "no 'Resources'/'resourceType' node configuration, no connection-level ResourceTypes, no SMART " +
                    "scopes to derive one from, and no reachable destination's own resource selection to fall back " +
                    "to. Configure at least one resource type for this node or its destination.");
        }

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
        //
        // A type outside the Patient compartment (Practitioner, Organization, Location, ... — see
        // PatientCompartmentResourceTypes) is never patient-scoped: no patient=/identifier= search is valid for it.
        // Manually selecting one of these still fetches it (see the per-type branch below), just via a single clean
        // unscoped request instead of the cohort-scoped path every compartment sibling uses.
        //
        // ReferenceScopedResourceTypes (Practitioner/Organization/Medication) and PractitionerScopedSearchResourceTypes
        // (PractitionerRole) run LAST, after every other selected type, because their fetch (see
        // FetchByCollectedReferencesAsync / FetchPractitionerRoleByCollectedPractitionersAsync) is built from
        // reference ids collected out of this node's already-fetched resources — those resources have to exist first.
        var executionOrder = resourceTypes
            .OrderBy(type => string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase)
                ? 0
                : ReferenceScopedResourceTypes.Contains(type) || PractitionerScopedSearchResourceTypes.Contains(type) ? 2 : 1)
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

        // A Group export scoped to just Patient hits the Epic bug above (business-rule 59159) — but only because
        // Group export makes Epic resolve membership internally. Resolving the Group's membership ourselves via a
        // plain FHIR read and re-issuing the job as a Patient-scoped export (with those ids and _type=Patient)
        // sidesteps the bug entirely, AND stops Epic from processing every other resource type it's authorized
        // for just to hand back the one type this node actually wants.
        //
        // The Group id used for a Bulk Data $export isn't guaranteed to be a normal, GET-able FHIR Group resource
        // (confirmed on Epic: some Group export ids 404 on a plain read despite being valid for $export) — when
        // membership can't be resolved that way, fall back to requesting Patient alongside ONE other
        // already-authorized resource type (known from this session's granted SMART scopes, not read from
        // anywhere) instead of omitting _type entirely. Epic's bug only triggers when Patient is the sole type, so
        // two types still avoids it, while limiting Epic's work to 2 resource types instead of every authorized
        // one. groupExportRequestTypes (not executionOrder) carries that throwaway type into the request only —
        // executionOrder below stays exactly ["Patient"], so the throwaway type's fetched data is never looked up
        // from batchedBulkResourcesByType, never added to this node's output, never processed further.
        var groupExportRequestTypes = executionOrder;
        if (explicitSystemOrGroupExport
            && BulkExportScopes.Parse(source.ExportScope) == BulkExportScope.Group
            && executionOrder is [{ } onlyResourceType] && string.Equals(onlyResourceType, "Patient", StringComparison.OrdinalIgnoreCase))
        {
            var groupPatientIds = await ResolveGroupPatientIdsAsync(client, source, context, node, cancellationToken);
            if (groupPatientIds.Count > 0)
            {
                source = source with { ExportScope = "patient", PatientIds = groupPatientIds, GroupId = null };
                explicitSystemOrGroupExport = false;
            }
            else if (PickThrowawayResourceType(source) is { } throwawayResourceType)
            {
                groupExportRequestTypes = [onlyResourceType, throwawayResourceType];
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>>? batchedBulkResourcesByType = null;
        if (explicitSystemOrGroupExport)
        {
            var batchedRequest = BuildBatchedBulkExportRequest(source, groupExportRequestTypes);

            // A System/Group export is exactly one $export job for this whole node (every resource type batched
            // into it, per the comment above) — the one shape simple enough to defer safely. Kick it off and hand
            // back a sentinel output instead of blocking this node's execution (and so the whole DAG run) for up
            // to the job's ~2-hour worst case; a BulkExportPollWorker tick resumes the run once it completes.
            // Only possible when this source resolves back to a real SourceConnection (so a poll tick can
            // re-resolve credentials later) and the job repository is actually wired — otherwise fall through to
            // the original blocking call, unchanged.
            if (_bulkExportJobRepository is not null && source.SourceConnectionId is { } deferrableSourceConnectionId)
            {
                // Client-side concurrency guard: every Bulk Data server enforces its own per-connection concurrency
                // cap server-side (e.g. athenahealth's 2/practice Preview, 5/practice Production) — checking the
                // durable job table first means a caller at capacity finds out via a fast local check instead of
                // only ever discovering it via a reactive 429 from the source server.
                var activeJobCount = await _bulkExportJobRepository.CountActiveBySourceConnectionAsync(deferrableSourceConnectionId, cancellationToken);
                if (activeJobCount >= _bulkExportConcurrencyOptions.MaxConcurrentJobsPerSourceConnection)
                {
                    throw new FHIRBridge.SharedKernel.Exceptions.BulkExportConcurrencyLimitExceededException(
                        TimeSpan.FromSeconds(_bulkExportConcurrencyOptions.RetryAfterSeconds));
                }

                var statusUrl = await _bulkExportClient!.KickOffExportAsync(batchedRequest, source, cancellationToken);
                var job = new FHIRBridge.Domain.Entities.BulkExportJob(
                    Guid.NewGuid(),
                    FHIRBridge.Domain.Entities.BulkExportJobSourcePath.WorkflowNode,
                    deferrableSourceConnectionId,
                    sourceConfigurationId: null,
                    exportRequestJson: System.Text.Json.JsonSerializer.Serialize(batchedRequest),
                    kickedOffOnUtc: DateTime.UtcNow,
                    correlationId: context.CorrelationId,
                    triggeredBy: context.TriggeredBy,
                    workflowRunId: context.WorkflowRunId,
                    workflowNodeId: node.Id,
                    contextJson: System.Text.Json.JsonSerializer.Serialize(context),
                    requestedResourceTypesJson: System.Text.Json.JsonSerializer.Serialize(executionOrder));
                job.MarkKickedOff(statusUrl);
                await _bulkExportJobRepository.AddAsync(job, cancellationToken);

                return new WorkflowNodeOutput(
                    node.Id,
                    node.NodeType,
                    payload: null,
                    WorkflowDataContract.None,
                    new Dictionary<string, object?>
                    {
                        [WorkflowNodeOutputMetadataKeys.BulkExportDeferredJobId] = job.Id.ToString(),
                    });
            }

            var batchedResources = await _bulkExportClient!.ExportAsync(batchedRequest, source, cancellationToken);
            batchedBulkResourcesByType = batchedResources
                .GroupBy(resource => resource.ResourceType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> (group) => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        // Real-granted-scope check: source.Scopes is only what FHIRBridge itself requested/configured — the IdP can
        // silently narrow that at token time (an interactive user declining a scope on Epic's consent screen, or a
        // backend-services registration the IdP only partially approved). Reading back what the token endpoint
        // actually granted (cheap for Backend Services, which can mint/reuse a cached token on demand; a pure cache
        // read for the interactive types, which cannot mint one without a user present) lets this pre-flight check
        // catch that narrowing directly instead of only ever discovering it via a reactive 401/403 per resource type.
        // Best-effort and additive only: unavailable (no provider wired, nothing cached yet, or the IdP never echoes
        // a scope back — e.g. Epic's SMART Backend Services token response commonly omits it) falls back to
        // source.Scopes, i.e. exactly the pre-existing configured-vs-configured check.
        string? actualGrantedScope = null;
        if (_accessTokenProvider is IFhirGrantedScopeProvider grantedScopeProvider)
        {
            try
            {
                actualGrantedScope = await grantedScopeProvider.GetGrantedScopeAsync(source, cancellationToken);
            }
            catch
            {
                // Token acquisition itself may legitimately fail here (e.g. an interactive source with no session
                // yet) — the reactive path below (and the existing configured-scope check) still applies unchanged.
            }
        }

        var effectiveGrantedScopes = !string.IsNullOrWhiteSpace(actualGrantedScope)
            ? actualGrantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : source.Scopes;

        // Scope-narrowing check: only worth a governance log entry (and the reference id that comes with it) when
        // the IdP's actual grant (actualGrantedScope, read back above) is missing something FHIRBridge configured
        // — i.e. the token endpoint silently narrowed the request. When actualGrantedScope is unavailable,
        // effectiveGrantedScopes just falls back to source.Scopes itself, so there is nothing to compare and
        // nothing anomalous to report. This deliberately does NOT log on every run: a fully-granted session is not
        // an error and must never mint a reference id (docs/ERRORS_SCREEN_CATEGORIZATION_ANALYSIS.md). Best-effort:
        // a governance-logging hiccup must never abort the extraction itself (CaptureExpectedAsync swallows
        // internally).
        if (_exceptionManager is not null && !string.IsNullOrWhiteSpace(actualGrantedScope))
        {
            var missingScopes = (source.Scopes ?? [])
                .Where(scope => !effectiveGrantedScopes.Contains(scope, StringComparer.Ordinal))
                .ToList();

            if (missingScopes.Count > 0)
            {
                var grantedScopesSummary = effectiveGrantedScopes is { Count: > 0 }
                    ? string.Join(", ", effectiveGrantedScopes)
                    : "(none granted)";
                await _exceptionManager.CaptureExpectedAsync(
                    new ExpectedFailure(
                        "ScopesNarrowed",
                        $"Authorization session for node '{node.Id}' ({node.NodeType}) granted fewer scopes than configured: missing = [{string.Join(", ", missingScopes)}]; granted = [{grantedScopesSummary}]; requested resource types = [{string.Join(", ", executionOrder)}]"),
                    new ExceptionContext(
                        Module: "Workflow",
                        Severity: "Informational",
                        CorrelationId: context.CorrelationId,
                        WorkflowId: node.WorkflowDefinitionId.ToString(),
                        ExecutionId: context.WorkflowRunId.ToString()),
                    cancellationToken);
            }
        }

        // Only enforced when the connection actually declares resource-scoped SMART grants — a source with no
        // scopes at all (e.g. Sample, or a non-interactive connection FHIRBridge doesn't track scopes for) has
        // nothing to check against, so every resource type it's configured for is attempted unchanged.
        var grantedResourceScopes = DeriveResourceTypesFromScopes(effectiveGrantedScopes);

        IReadOnlyList<string>? cohortPatientIds = null;
        var resources = new List<ResourceEnvelope>();
        var skippedResourceTypes = new List<string>();
        var syncedResourceTypes = new List<string>();
        foreach (var type in executionOrder)
        {
            var isPatientType = string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> page;
            try
            {
                if (grantedResourceScopes.Count > 0 && !IsResourceTypeAuthorized(type, effectiveGrantedScopes))
                {
                    // Caught immediately below — same Patient(cancel)/child(skip) handling as a reactive 401/403
                    // from the FHIR server, except this fires before any request is sent, so a known-missing scope
                    // never even attempts (and never waits on) a call the server would have rejected anyway.
                    throw new FHIRBridge.Runtime.Domain.Exceptions.ResourceAuthorizationException(
                        type,
                        0,
                        $"no granted SMART scope authorizes '{type}' for this session (granted: {(effectiveGrantedScopes is { Count: > 0 } ? string.Join(", ", effectiveGrantedScopes) : "none")})");
                }

                // Device is not a genuine FHIR-spec patient-compartment member (see PatientCompartmentResourceTypes'
                // own doc comment) and is deliberately excluded from that shared, spec-accurate list — but Epic's
                // own business-rule validator (code 59108, "A patient is required") rejects an unscoped Device
                // search anyway. Scoped as an Epic-only addition here, rather than added to the shared domain-level
                // list, since that list is also relied on elsewhere for spec-accurate compartment membership.
                var isCompartmentType = PatientCompartmentResourceTypes.IsSupported(type)
                    || (_sourceType == RuntimeSourceType.Epic && string.Equals(type, "Device", StringComparison.OrdinalIgnoreCase));

                page = useBulkExport
                    ? batchedBulkResourcesByType is not null
                        ? batchedBulkResourcesByType.TryGetValue(type, out var batchedPage) ? batchedPage : []
                        : await _bulkExportClient!.ExportAsync(
                            await BuildBulkExportRequestAsync(
                                source, type, isPatientType ? null : cohortPatientIds, context.WorkflowRunId, cancellationToken),
                            source,
                            cancellationToken)
                    : PractitionerScopedSearchResourceTypes.Contains(type)
                        // Epic rejects both an unscoped and a patient-scoped PractitionerRole search ("An identifier,
                        // practitioner, organization, location, or specialty parameter is required" — business rule
                        // 59108) — unlike ReferenceScopedResourceTypes below, a direct GET PractitionerRole/{id}
                        // isn't an option either, since nothing in the batch embeds a "PractitionerRole/{id}"
                        // reference (other resources reference the Practitioner itself, not their PractitionerRole).
                        // Scoped instead by re-searching once per distinct Practitioner id this node's own
                        // already-fetched resources reference — the same "practitioner" parameter Epic's error
                        // names as one of the accepted ways to satisfy this rule.
                        ? await FetchPractitionerRoleByCollectedPractitionersAsync(client, source, resources, context, cancellationToken)
                        : ReferenceScopedResourceTypes.Contains(type)
                        // Epic rejects both an unscoped and a patient-scoped search for these types (confirmed live:
                        // "Only an _ID search is allowed" for Organization/Medication, "Either name, family, or
                        // identifier is a required parameter" for Practitioner) — the only reliable fetch is a
                        // direct-by-id read per id this node's already-fetched resources actually reference.
                        ? await FetchByCollectedReferencesAsync(client, type, source, resources, context, node, cancellationToken)
                        : isPatientType || cohortPatientIds is not { Count: > 0 } || !isCompartmentType
                            // A type outside the Patient compartment gets one single, clean request — no leftover
                            // Patient SearchParameters/PatientSearchCriteria/PatientIds carried over (that reuse was the
                            // actual cause of Epic's historical "a required parameter is missing" 400s for these types,
                            // not the fact of fetching them directly), and no per-cohort-patient looping (which would
                            // otherwise fire the identical unscoped request once per patient via SearchCohortScopedAsync).
                            ? await SearchWithPolicyAsync(
                                client,
                                type,
                                isPatientType || isCompartmentType
                                    ? source
                                    : source with { SearchParameters = null, PatientIds = null, TargetPatientId = null, PatientSearchCriteria = null },
                                context.WorkflowRunId,
                                cancellationToken)
                            : await SearchCohortScopedAsync(client, type, source, cohortPatientIds, context.WorkflowRunId, cancellationToken);
            }
            catch (Exception extractionFailure) when (extractionFailure is FHIRBridge.Runtime.Domain.Exceptions.IResourceExtractionFailure failure)
            {
                // Patient (or whichever type seeds the cohort) is the parent every sibling resource type here is
                // scoped off — if it can't be fetched (unauthorized, or not supported by this source at all),
                // there is no partial result to isolate: cancel the whole run rather than silently running the
                // other types unscoped or not at all. A non-parent type failing the same way just means less
                // data, not an unrunnable workflow, so it's skipped and the rest of the node's fetch continues.
                if (isPatientType)
                {
                    throw new FHIRBridge.Runtime.Domain.Exceptions.WorkflowRunCancelledException(
                        type, extractionFailure.Message, extractionFailure);
                }

                skippedResourceTypes.Add(
                    $"{type}: {failure.SkipReasonLabel} ({failure.StatusCode}) — {extractionFailure.Message}");
                continue;
            }

            resources.AddRange(page.Select(resource => new ResourceEnvelope(
                resource.ResourceType,
                resource.ResourceId ?? string.Empty,
                resource.RawJson)));

            // Only a type that actually completed (didn't throw/get skipped above) advances its own cursor — a
            // sibling type failing this run must not affect this one, and vice versa.
            syncedResourceTypes.Add(type);

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

        if (source.SourceConnectionId is { } resolvedSourceConnectionId && _syncCursorStore is not null && syncedResourceTypes.Count > 0)
        {
            await _syncCursorStore.RecordSuccessfulSyncAsync(resolvedSourceConnectionId, syncedResourceTypes, DateTime.UtcNow, cancellationToken);
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
                // Per-type breakdown of the combined "count" above — without this, a multi-resource-type source
                // node's execution history can only ever say "extracted 501 resources total", leaving no way to
                // tell (short of decrypting the recorded payload) whether one specific resource type genuinely
                // returned zero results from the EHR versus something downstream silently dropping its records.
                ["resourceTypeCounts"] = resources
                    .GroupBy(resource => resource.ResourceType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
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

    /// <summary>Appends one FHIR search parameter onto an existing (possibly null/blank) search-parameters string.</summary>
    private static string AppendSearchParameter(string? searchParameters, string parameter) =>
        string.IsNullOrWhiteSpace(searchParameters)
            ? parameter
            : $"{searchParameters.TrimEnd('&', '?')}&{parameter}";

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
        var destinationResourceTypes = await GetDestinationResourceTypesAsync(node, cancellationToken);
        return destinationResourceTypes.Count == 0
            ? resourceTypes
            : resourceTypes.Where(destinationResourceTypes.Contains).ToList();
    }

    /// <summary>
    /// Every FHIR resource type any destination reachable from this source node has declared via its own
    /// wizard-authored "dest_resources" field — unioned across all such destinations. Shared by
    /// <see cref="RestrictToDestinationResourceTypesAsync"/> (uses this to narrow an already-resolved list) and the
    /// resource-type resolution fallback chain in <see cref="ExecuteAsync"/> (uses this to seed one from scratch
    /// when the source itself has no configured/connection-level/scope-derived resource types of its own — e.g. a
    /// Generic FHIR source, which has no OAuth scopes to derive anything from). Returns an empty set (never throws)
    /// when there's no workflow store, no workflow definition, or no reachable destination at all.
    /// <para>
    /// Purely what the wizard's "dest_resources" field lists — no types are added beyond that. A type outside the
    /// Patient compartment (Practitioner, Organization, Location, ...) that isn't checked here is genuinely excluded
    /// from fetching, even if another selected type references it — a deliberate tradeoff in favor of predictable,
    /// manual control over what gets fetched, over automatically resolving dangling references. See
    /// <c>MappedFhirRepositoryDestinationWriter.DetectMissingReferencedTypes</c> for the write-time warning that
    /// surfaces this kind of gap before it reaches Aidbox as a raw 422.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyCollection<string>> GetDestinationResourceTypesAsync(
        WorkflowNode node,
        CancellationToken cancellationToken)
    {
        if (_workflowDefinitionStore is null)
        {
            return [];
        }

        var definition = await _workflowDefinitionStore.GetAsync(node.WorkflowDefinitionId, cancellationToken);
        if (definition is null)
        {
            return [];
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

        return destinationResourceTypes;
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

    /// <summary>Picks one already-authorized resource type other than "Patient" — purely to ride alongside Patient
    /// in a Group $export's <c>_type</c> so Epic's lone-Patient bug (business-rule 59159) doesn't trigger. Never
    /// derived from what the workflow actually wants (that's <c>executionOrder</c>) — this is throwaway, its data
    /// is discarded before this node's output is built (see the call site). Deterministic (first match in scope
    /// order) so the same connection always picks the same type, not a random one each run. Null when the only
    /// granted scope is Patient itself — nothing else to add, so the caller keeps the pre-existing omit-_type
    /// behavior as a last resort.</summary>
    private static string? PickThrowawayResourceType(FhirSourceConfiguration source)
    {
        return DeriveResourceTypesFromScopes(source.Scopes)
            .FirstOrDefault(type => !string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True if at least one of <paramref name="grantedScopes"/> covers <paramref name="resourceType"/> — same scope
    /// shape as <see cref="DeriveResourceTypesFromScopes"/> (<c>{context}/{ResourceType}.{permissions}</c>), plus a
    /// bare wildcard resource segment (<c>system/*.read</c>, <c>user/*.rs</c>) counting as covering everything.
    /// </summary>
    private static bool IsResourceTypeAuthorized(string resourceType, IReadOnlyCollection<string> grantedScopes)
    {
        foreach (var scope in grantedScopes)
        {
            var slashIndex = scope.IndexOf('/');
            if (slashIndex < 0 || slashIndex == scope.Length - 1)
            {
                continue;
            }

            var afterSlash = scope[(slashIndex + 1)..];
            var dotIndex = afterSlash.IndexOf('.');
            var candidate = dotIndex >= 0 ? afterSlash[..dotIndex] : afterSlash;

            if (candidate == "*" || string.Equals(candidate, resourceType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        // Each resource type is fetched via its own independent search request, so each carries its own
        // _lastUpdated watermark rather than the connection-wide value every type used to share — a type with no
        // watermark of its own (first run, or previously skipped) falls back to an unfiltered full pull. Applied
        // here (the single choke point both the direct and cohort-scoped search paths funnel through), not earlier
        // in ExecuteAsync, because SearchCohortScopedAsync deliberately clears SearchParameters for sibling types —
        // baking the watermark into that string upstream would have been wiped out along with it.
        var effectiveSource = source.LastUpdatedWatermarks?.TryGetValue(resourceType, out var watermark) == true
            ? source with { SearchParameters = AppendSearchParameter(source.SearchParameters, $"_lastUpdated=gt{watermark:yyyy-MM-ddTHH:mm:ssZ}") }
            : source;

        var maxAttempts = effectiveSource.RetryPolicy switch
        {
            "fixed-3" => 3,
            "exponential" => 3,
            _ => 1,
        };

        for (var attempt = 1; ; attempt++)
        {
            using var timeoutCts = effectiveSource.TimeoutSeconds is { } timeoutSeconds
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(effectiveSource.TimeoutSeconds!.Value));

            try
            {
                var result = await client.SearchAsync(resourceType, effectiveSource, timeoutCts?.Token ?? cancellationToken);
                return result;
            }
            catch (Exception ex) when (ex is not FHIRBridge.Runtime.Domain.Exceptions.IResourceExtractionFailure
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
            ResourceTypes: BulkExportScopes.ResolveTypeParameter(scope, resourceTypes),
            Since: source.Since,
            PatientIds: null,
            OutputFormat: source.OutputFormat);
    }

    // Reads the Group resource itself (a plain FHIR GET, not a bulk job) and pulls out its member Patient ids —
    // lets a Group export scoped to just "Patient" be re-issued as a Patient-scoped export instead (see the
    // call site above), sidestepping the Epic Group-export bug entirely rather than working around it by fetching
    // every resource type. Best-effort: this app may not be granted Group.read, the Group id may not exist on
    // this server, or the read may simply fail transiently — any of those just falls back to the pre-existing
    // Group-scoped behavior (unscoped, every resource type), never breaks the run.
    private async Task<IReadOnlyList<string>> ResolveGroupPatientIdsAsync(
        IFhirSourceClient client,
        FhirSourceConfiguration source,
        WorkflowExecutionContext context,
        WorkflowNode node,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.GroupId))
        {
            return [];
        }

        try
        {
            // A direct read, not a search — confirmed on Epic that Group?_id=X can reject an id
            // (OperationOutcome "Invalid FHIR ID provided" / "No valid FHIR IDs provided") that GET Group/X
            // resolves without issue, even though it's the exact same id the $export kickoff URL already uses.
            var group = await client.ReadByIdAsync("Group", source.GroupId, source, cancellationToken);
            return group is null ? [] : ExtractMemberPatientIds(group.RawJson);
        }
        catch (Exception exception)
        {
            if (_exceptionManager is not null)
            {
                await _exceptionManager.CaptureExpectedAsync(
                    new ExpectedFailure(
                        "GroupMembershipResolutionFailed",
                        $"Could not resolve Group '{source.GroupId}' membership ({exception.Message}); falling back to an unscoped Group export."),
                    new ExceptionContext(
                        Module: "Workflow",
                        Severity: "Informational",
                        CorrelationId: context.CorrelationId,
                        WorkflowId: node.WorkflowDefinitionId.ToString(),
                        ExecutionId: context.WorkflowRunId.ToString()),
                    cancellationToken);
            }

            return [];
        }
    }

    private static IReadOnlyList<string> ExtractMemberPatientIds(string groupRawJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(groupRawJson);
        if (!document.RootElement.TryGetProperty("member", out var members) ||
            members.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var member in members.EnumerateArray())
        {
            if (member.TryGetProperty("entity", out var entity) &&
                entity.TryGetProperty("reference", out var referenceProperty) &&
                referenceProperty.ValueKind == System.Text.Json.JsonValueKind.String &&
                referenceProperty.GetString() is { Length: > 0 } reference &&
                reference.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(reference["Patient/".Length..]);
            }
        }

        return ids;
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
                ResourceTypes: BulkExportScopes.ResolveTypeParameter(configuredScope, [resourceType]),
                Since: source.Since,
                PatientIds: null,
                OutputFormat: source.OutputFormat);
        }

        var effectiveScope = hasCohort ? BulkExportScope.Patient : configuredScope;
        return new FhirBulkExportRequest(
            effectiveScope,
            GroupId: effectiveScope == BulkExportScope.Group ? source.GroupId : null,
            ResourceTypes: BulkExportScopes.ResolveTypeParameter(effectiveScope, [resourceType]),
            Since: source.Since,
            PatientIds: effectiveScope == BulkExportScope.Patient ? (cohortPatientIds ?? source.PatientIds) : null,
            OutputFormat: source.OutputFormat);
    }

    /// <summary>
    /// Resource types Epic accepts neither an unscoped nor a <c>patient=</c>-scoped search for — confirmed live:
    /// Organization/Medication/Specimen/Questionnaire reject anything but <c>_id=</c> ("Only an _ID search is
    /// allowed" — business rule 59102 for Questionnaire), Practitioner requires <c>name</c>/<c>family</c>/
    /// <c>identifier</c> ("A required element is missing"), and Location requires "at least one parameter besides
    /// status" (an unscoped/status-only request isn't enough). None of those are values this node has on hand for
    /// an arbitrary tenant-wide fetch, so these are instead resolved by id — see
    /// <see cref="FetchByCollectedReferencesAsync"/>. Questionnaire ids come from
    /// <c>QuestionnaireResponse.questionnaire</c> references, so a route selecting Questionnaire needs
    /// QuestionnaireResponse selected alongside it — same requirement as Organization/Medication needing a sibling
    /// that actually references them.
    /// </summary>
    private static readonly HashSet<string> ReferenceScopedResourceTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Practitioner", "Organization", "Medication", "Location", "Specimen", "Questionnaire" };

    /// <summary>
    /// Resource types Epic rejects both an unscoped and a <c>patient=</c>-scoped search for, but — unlike
    /// <see cref="ReferenceScopedResourceTypes"/> — can't be resolved by a direct-by-id read either, since nothing
    /// in the batch embeds a reference to their own type: <c>PractitionerRole</c> requires "an identifier,
    /// practitioner, organization, location, or specialty parameter" (business rule 59108), but other resources
    /// reference the underlying <c>Practitioner</c> directly, never a <c>PractitionerRole/{id}</c>. See
    /// <see cref="FetchPractitionerRoleByCollectedPractitionersAsync"/>.
    /// </summary>
    private static readonly HashSet<string> PractitionerScopedSearchResourceTypes =
        new(StringComparer.OrdinalIgnoreCase) { "PractitionerRole" };

    /// <summary>
    /// Fetches PractitionerRole by re-searching once per distinct Practitioner id this node's own already-fetched
    /// resources reference (e.g. <c>Encounter.participant</c>, <c>DocumentReference.author</c>), scoped by
    /// <c>practitioner=Practitioner/{id}</c> — one of the parameters Epic's own business-rule error names as
    /// sufficient. Falls back to a single clean unscoped search (same as
    /// <see cref="FetchByCollectedReferencesAsync"/>'s fallback) when nothing in the batch references a Practitioner
    /// at all; Epic will still reject that fallback per the same business rule, same as before this method existed.
    /// Results are deduped by resource id — the same PractitionerRole can legitimately turn up for more than one
    /// Practitioner id search (e.g. a role covering several locations, each surfaced via a different encounter).
    /// </summary>
    private async Task<IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>> FetchPractitionerRoleByCollectedPractitionersAsync(
        IFhirSourceClient client,
        FhirSourceConfiguration source,
        IReadOnlyList<ResourceEnvelope> alreadyFetched,
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var practitionerIds = ExtractReferencedIds(alreadyFetched, "Practitioner");
        if (practitionerIds.Count == 0)
        {
            var cleanSource = source with
            {
                SearchParameters = null,
                PatientIds = null,
                TargetPatientId = null,
                PatientSearchCriteria = null,
            };
            return await SearchWithPolicyAsync(client, "PractitionerRole", cleanSource, context.WorkflowRunId, cancellationToken);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>();
        foreach (var practitionerId in practitionerIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scopedSource = source with
            {
                SearchParameters = $"practitioner=Practitioner/{practitionerId}",
                PatientIds = null,
                TargetPatientId = null,
                PatientSearchCriteria = null,
            };

            IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> page;
            try
            {
                page = await SearchWithPolicyAsync(client, "PractitionerRole", scopedSource, context.WorkflowRunId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One Practitioner's PractitionerRole search failing (unauthorized, transient) only drops the
                // roles tied to that one practitioner, not the whole resource type.
                continue;
            }

            foreach (var resource in page)
            {
                if (!string.IsNullOrWhiteSpace(resource.ResourceId) && !seenIds.Add(resource.ResourceId))
                {
                    continue;
                }

                results.Add(resource);
            }
        }

        return results;
    }

    /// <summary>
    /// Fetches <paramref name="resourceType"/> by reading each id this node's own already-fetched resources
    /// reference (e.g. <c>Patient.generalPractitioner</c>, <c>Encounter.serviceProvider</c>,
    /// <c>MedicationRequest.medicationReference</c>) — a direct <c>GET {type}/{id}</c> per id, sidestepping Epic's
    /// per-type search restrictions entirely (see <see cref="ReferenceScopedResourceTypes"/>). Falls back to the
    /// pre-existing single unscoped search when nothing in the batch references this type at all (e.g. it was
    /// selected on its own, with no compartment sibling to source ids from) — with the same leftover Patient-scoped
    /// SearchParameters/PatientIds/TargetPatientId/PatientSearchCriteria cleared before that fallback search that
    /// the sibling non-compartment-type branch above already clears (reusing stale Patient search criteria was the
    /// actual historical cause of Epic's "a required parameter is missing" 400s for these types, not the fact of
    /// fetching them directly) — Epic may still reject the fallback search depending on the type's own requirements,
    /// same as before this change. One id's read failing (unauthorized, deleted, transient) only drops that one
    /// referenced resource, not the whole type.
    /// </summary>
    private async Task<IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>> FetchByCollectedReferencesAsync(
        IFhirSourceClient client,
        string resourceType,
        FhirSourceConfiguration source,
        IReadOnlyList<ResourceEnvelope> alreadyFetched,
        WorkflowExecutionContext context,
        WorkflowNode node,
        CancellationToken cancellationToken)
    {
        var referencedIds = ExtractReferencedIds(alreadyFetched, resourceType);
        if (referencedIds.Count == 0)
        {
            var cleanSource = source with
            {
                SearchParameters = null,
                PatientIds = null,
                TargetPatientId = null,
                PatientSearchCriteria = null,
            };
            return await SearchWithPolicyAsync(client, resourceType, cleanSource, context.WorkflowRunId, cancellationToken);
        }

        var results = new List<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>();
        foreach (var id in referencedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resource = await client.ReadByIdAsync(resourceType, id, source, cancellationToken);
                if (resource is not null)
                {
                    results.Add(resource);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_exceptionManager is not null)
                {
                    await _exceptionManager.CaptureExpectedAsync(
                        new ExpectedFailure(
                            "ReferencedResourceReadFailed",
                            $"Could not read {resourceType}/{id} ({exception.Message}); skipping this referenced id."),
                        new ExceptionContext(
                            Module: "Workflow",
                            Severity: "Informational",
                            CorrelationId: context.CorrelationId,
                            WorkflowId: node.WorkflowDefinitionId.ToString(),
                            ExecutionId: context.WorkflowRunId.ToString()),
                        cancellationToken);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Resource types whose reference back to <see cref="ReferenceScopedResourceTypes"/>/
    /// <see cref="PractitionerScopedSearchResourceTypes"/> members isn't a standard FHIR <c>Reference</c> datatype
    /// (<c>{"reference": "Type/id"}</c>) but a bare <c>canonical</c> string property instead — e.g.
    /// <c>QuestionnaireResponse.questionnaire</c> is typed <c>canonical(Questionnaire)</c> in the R4 spec, so it's
    /// serialized as a top-level <c>"questionnaire": "..."</c> string with no nested <c>"reference"</c> key at all,
    /// which <see cref="CollectReferencedIds"/>'s ordinary walk structurally cannot see. Maps the target resource
    /// type to the exact property name holding its canonical reference.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CanonicalReferencePropertyByResourceType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Questionnaire"] = "questionnaire" };

    /// <summary>
    /// Every distinct id referenced as <c>{targetResourceType}/{id}</c> anywhere within <paramref name="resources"/>'
    /// raw JSON — walks the full document tree since the reference can sit at any depth/shape depending on the
    /// referencing resource type (a flat property, or nested inside an array of backbone elements). Also matches a
    /// bare canonical-string reference (see <see cref="CanonicalReferencePropertyByResourceType"/>) when
    /// <paramref name="targetResourceType"/> has one — matched by the last <c>{targetResourceType}/</c> occurrence
    /// in the string rather than requiring it to start there, since a canonical value is commonly a full absolute
    /// URL (e.g. <c>http://tenant.example.org/fhir/Questionnaire/123</c>) rather than a bare relative reference.
    /// </summary>
    private static HashSet<string> ExtractReferencedIds(IReadOnlyList<ResourceEnvelope> resources, string targetResourceType)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var prefix = $"{targetResourceType}/";
        CanonicalReferencePropertyByResourceType.TryGetValue(targetResourceType, out var canonicalProperty);

        foreach (var resource in resources)
        {
            if (resource.Payload is not string rawJson || string.IsNullOrWhiteSpace(rawJson))
            {
                continue;
            }

            using var document = System.Text.Json.JsonDocument.Parse(rawJson);
            CollectReferencedIds(document.RootElement, prefix, canonicalProperty, ids);
        }

        return ids;
    }

    private static void CollectReferencedIds(
        System.Text.Json.JsonElement element,
        string referencePrefix,
        string? canonicalProperty,
        HashSet<string> ids)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "reference", StringComparison.Ordinal) &&
                        property.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                        property.Value.GetString() is { Length: > 0 } reference &&
                        reference.StartsWith(referencePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        ids.Add(reference[referencePrefix.Length..]);
                    }
                    else if (canonicalProperty is not null &&
                        string.Equals(property.Name, canonicalProperty, StringComparison.Ordinal) &&
                        property.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                        property.Value.GetString() is { Length: > 0 } canonicalValue)
                    {
                        var lastOccurrence = canonicalValue.LastIndexOf(referencePrefix, StringComparison.OrdinalIgnoreCase);
                        if (lastOccurrence >= 0)
                        {
                            // Strip a trailing "|version" if present — canonical references may pin a specific
                            // version of the target, which a plain GET {Type}/{id} has no use for.
                            var idStart = lastOccurrence + referencePrefix.Length;
                            var id = canonicalValue[idStart..];
                            var versionPipe = id.IndexOf('|');
                            if (versionPipe >= 0)
                            {
                                id = id[..versionPipe];
                            }

                            if (id.Length > 0)
                            {
                                ids.Add(id);
                            }
                        }
                    }
                    else
                    {
                        CollectReferencedIds(property.Value, referencePrefix, canonicalProperty, ids);
                    }
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectReferencedIds(item, referencePrefix, canonicalProperty, ids);
                }
                break;
        }
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new ResourceBatch([]);
}
