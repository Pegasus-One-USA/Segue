using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public sealed class DataQualityScoringNodeExecutor : PassThroughNodeExecutor
{
    public DataQualityScoringNodeExecutor(IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.DataQualityScoring, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class NormalizationNodeExecutor : PassThroughNodeExecutor
{
    public NormalizationNodeExecutor(IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.Normalization, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class FlattenExtensionsNodeExecutor : PassThroughNodeExecutor
{
    public FlattenExtensionsNodeExecutor(IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.FlattenExtensions, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class PatientMatchingNodeExecutor : PassThroughNodeExecutor
{
    public PatientMatchingNodeExecutor(IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.PatientMatching, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

/// <summary>
/// Executor for V2's consolidated "Transformation" chain step: applies
/// <see cref="TransformExecutionPhase.FhirResource"/> rules to each resource's own JSON, in place.
///
/// This is the only place the 20 transform nodes can run for a FHIR-native destination, which stores whole
/// resources and so has no mapped columns for a PostMapping rule to attach to. Kept as its own node type rather
/// than reusing <see cref="NormalizationNodeExecutor"/> (V1's "Normalize Data" step) precisely so this work can
/// never execute inside a V1 pipeline.
///
/// Falls back to plain passthrough whenever its dependencies aren't wired, the node names no destination, or no
/// rule matched — a resource is only ever replaced by a genuinely transformed one.
/// </summary>
public sealed class FhirResourceTransformNodeExecutor : PassThroughNodeExecutor
{
    private readonly IFhirResourceTransformService? _transformService;
    private readonly IConfigurationRepository? _configurationRepository;
    private readonly ILineageCaptureDispatcher? _lineageCaptureDispatcher;

    /// <summary>Needed to run a node's OWN rules: the inline path builds a transform service around an
    /// <see cref="InlineFhirResourceRuleResolver"/> rather than the repository-backed one, so it needs the same
    /// node registry (and optional secret accessor) that service is normally composed with. Null simply means
    /// the inline path is unavailable and the node resolves by route id as before.</summary>
    private readonly ITransformNodeRegistry? _transformNodeRegistry;
    private readonly IAppSecretAccessor? _secretAccessor;

    public FhirResourceTransformNodeExecutor(
        IFhirResourceTransformService? transformService = null,
        IConfigurationRepository? configurationRepository = null,
        ILineageCaptureDispatcher? lineageCaptureDispatcher = null,
        IResourceNormalizationService? normalizationService = null,
        ITransformNodeRegistry? transformNodeRegistry = null,
        IAppSecretAccessor? secretAccessor = null)
        : base(WorkflowNodeTypes.FhirResourceTransform, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
        _transformService = transformService;
        _configurationRepository = configurationRepository;
        _lineageCaptureDispatcher = lineageCaptureDispatcher;
        _transformNodeRegistry = transformNodeRegistry;
        _secretAccessor = secretAccessor;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        if (_transformService is null || _configurationRepository is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        // Self-contained first (plan §3.4): rules the node carries itself need no destination, no route id and
        // no repository — which is exactly why they cannot be orphaned, cross-wired between workflows, or left
        // inert by a null ResourcePipelineRouteId.
        var inlineRules = _transformNodeRegistry is null ? null : TryReadInlineRules(node);
        if (inlineRules is { Count: > 0 })
        {
            return await ExecuteWithInlineRulesAsync(context, node, inputs, inlineRules, cancellationToken);
        }

        // The destination decides which rules apply (its DestinationType is a resolver tier), so a node with no
        // destination stamped on it has nothing to resolve against and passes through untouched.
        Guid.TryParse(ReadStringConfiguration(node, "destinationId"), out var destinationId);
        if (destinationId == Guid.Empty)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
        if (destination is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        Guid.TryParse(ReadStringConfiguration(node, "sourceConnectionId"), out var sourceConnectionId);
        var sourceSystem = sourceConnectionId == Guid.Empty
            ? null
            : (await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken))
                ?.SourceSystemType.ToString();
        Guid.TryParse(ReadStringConfiguration(node, "resourcePipelineRouteId"), out var routeId);

        var transformed = new List<ResourceEnvelope>();
        var transformedCount = 0;
        var ruleErrors = new List<string>();

        foreach (var resource in ReadResourceEnvelopes(inputs))
        {
            var sourceJson = Convert.ToString(resource.Payload) ?? "{}";
            var result = await _transformService.TransformAsync(
                sourceJson, resource.ResourceType, resource.ResourceId, destination.DestinationType,
                routeId == Guid.Empty ? null : routeId, sourceSystem, cancellationToken);

            if (!ReferenceEquals(result.Json, sourceJson))
            {
                transformedCount++;
            }

            foreach (var hop in result.Hops.Where(hop => !hop.Success))
            {
                ruleErrors.Add($"{resource.ResourceType}/{resource.ResourceId} {hop.SourceField} [{hop.NodeType}]: {hop.Error}");
            }

            transformed.Add(resource with { Payload = result.Json });
            await CaptureLineageAsync(context, node, resource, result, destination, sourceSystem, cancellationToken);
        }

        if (ruleErrors.Count > 0)
        {
            // Warning, not Error: a failed rule hop degrades one field, it doesn't fail the run — so nothing
            // else reports it. FirstRuleError is included because the full list can be long and is already
            // preserved on the node's lineage metadata.
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                Logger,
                FHIRBridge.Observability.Logging.LogEvents.TransformCompleted,
                "Transform applied to {RecordCount} resource(s); {TransformedCount} changed, "
                + "{RuleErrorCount} rule hop(s) failed. FirstRuleError={FirstRuleError}",
                transformed.Count, transformedCount, ruleErrors.Count, ruleErrors[0]);
        }
        else
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
                Logger,
                FHIRBridge.Observability.Logging.LogEvents.TransformCompleted,
                "Transform applied to {RecordCount} resource(s); {TransformedCount} changed.",
                transformed.Count, transformedCount);
        }

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new NormalizedResourceBatch(transformed),
            OutputContract,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["count"] = transformed.Count,
                ["transformed"] = transformedCount,
                ["ruleErrors"] = ruleErrors
            });
    }

    /// <summary>
    /// Runs the node's OWN rules — no destination lookup, no source-connection lookup, no route id, no
    /// repository. Everything this needs is on the node, which is the whole point of the self-contained model:
    /// the rules cannot be orphaned by a null route id, cannot be swept up by another workflow's save, and
    /// cannot be changed by an edit to a shared master record.
    /// </summary>
    private async Task<WorkflowNodeOutput> ExecuteWithInlineRulesAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        IReadOnlyList<TransformationRule> inlineRules,
        CancellationToken cancellationToken)
    {
        var resolver = new InlineFhirResourceRuleResolver(inlineRules);
        var transformService = new FhirResourceTransformService(resolver, _transformNodeRegistry!, _secretAccessor);

        var transformed = new List<ResourceEnvelope>();
        var transformedCount = 0;
        var ruleErrors = new List<string>();

        foreach (var resource in ReadResourceEnvelopes(inputs))
        {
            var sourceJson = Convert.ToString(resource.Payload) ?? "{}";

            // DestinationType is required by the service signature but is never consulted by the inline
            // resolver — these rules already belong to exactly one node, so there is nothing to filter by.
            var result = await transformService.TransformAsync(
                sourceJson, resource.ResourceType, resource.ResourceId, DestinationType.SqlServer,
                resourcePipelineRouteId: null, sourceSystem: null, cancellationToken);

            if (!ReferenceEquals(result.Json, sourceJson))
            {
                transformedCount++;
            }

            foreach (var hop in result.Hops.Where(hop => !hop.Success))
            {
                ruleErrors.Add($"{resource.ResourceType}/{resource.ResourceId} {hop.SourceField} [{hop.NodeType}]: {hop.Error}");
            }

            transformed.Add(resource with { Payload = result.Json });
        }

        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
            Logger,
            FHIRBridge.Observability.Logging.LogEvents.TransformCompleted,
            "Transform applied to {RecordCount} resource(s) from {RuleCount} inline rule(s); "
            + "{TransformedCount} changed, {RuleErrorCount} rule hop(s) failed.",
            transformed.Count, inlineRules.Count, transformedCount, ruleErrors.Count);

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new NormalizedResourceBatch(transformed),
            OutputContract,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["count"] = transformed.Count,
                ["transformed"] = transformedCount,
                ["ruleErrors"] = ruleErrors,
                ["inlineRules"] = inlineRules.Count,
            });
    }

    /// <summary>
    /// This node's own transformation rules, from the self-contained <c>rules</c> array (plan §3.4). Null when
    /// the node carries none — the caller then falls back to resolving them out of the shared table by route id,
    /// which is how every node behaves until it has been migrated.
    /// </summary>
    private static IReadOnlyList<TransformationRule>? TryReadInlineRules(WorkflowNode node)
    {
        var specs = ReadConfiguration<List<InlineRuleSpec>>(node, "rules");
        if (specs is null || specs.Count == 0)
        {
            return null;
        }

        var rules = new List<TransformationRule>(specs.Count);
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.SourceField) || !Enum.TryParse<TransformNodeType>(spec.NodeType, true, out var nodeType))
            {
                // A rule with no source field has nothing to read, and an unknown node type has nothing to run
                // it — skipping beats throwing, which would fail the whole run over one malformed entry.
                continue;
            }

            rules.Add(new TransformationRule(
                TransformScope.ResourceType,
                nodeType,
                spec.Config?.ToString() ?? "{}",
                resourceType: spec.ResourceType,
                destinationField: spec.DestinationField,
                sourceField: spec.SourceField,
                order: spec.Order,
                onNull: Enum.TryParse<NullPolicy>(spec.OnNull, true, out var onNull) ? onNull : NullPolicy.Skip,
                errorPolicy: Enum.TryParse<TransformErrorPolicy>(spec.ErrorPolicy, true, out var errorPolicy)
                    ? errorPolicy
                    : TransformErrorPolicy.NullOut,
                onNullDefaultValue: spec.OnNullDefaultValue,
                arrayMode: Enum.TryParse<TransformArrayMode>(spec.ArrayMode, true, out var arrayMode)
                    ? arrayMode
                    : TransformArrayMode.Whole,
                fhirWriteBackJsonPath: spec.FhirWriteBackJsonPath,
                executionPhase: TransformExecutionPhase.FhirResource));
        }

        return rules.Count > 0 ? rules : null;
    }

    /// <summary>One transformation rule as stored on the node. Mirrors the authoring shape in plan §3.4.</summary>
    private sealed class InlineRuleSpec
    {
        public string? ResourceType { get; set; }

        public string? SourceField { get; set; }

        public string? DestinationField { get; set; }

        public string? NodeType { get; set; }

        public int Order { get; set; }

        public string? OnNull { get; set; }

        public string? ErrorPolicy { get; set; }

        public string? OnNullDefaultValue { get; set; }

        public string? ArrayMode { get; set; }

        public string? FhirWriteBackJsonPath { get; set; }

        /// <summary>The node's own settings, kept as raw JSON — its shape is decided by NodeType.</summary>
        public System.Text.Json.JsonElement? Config { get; set; }
    }

    /// <summary>Fire-and-continue, matching MappingNodeExecutor: a lineage publish failure must never fail the
    /// resource's own transform, so it is swallowed rather than surfaced into the caller's exception path.</summary>
    private async Task CaptureLineageAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        ResourceEnvelope resource,
        FhirResourceTransformResult result,
        DestinationConfiguration destination,
        string? sourceSystem,
        CancellationToken cancellationToken)
    {
        if (_lineageCaptureDispatcher is null || result.Hops.Count == 0)
        {
            return;
        }

        // DestinationField carries the FHIR write-back path here — for a FHIR-native destination that IS the
        // "column" the value lands in, so the lineage view reads the same way it does for a SQL target.
        var entries = result.Hops
            .Select(hop => new LineageHopEntryDto(
                hop.WriteBackPath, hop.SourceField, hop.NodeOrder, hop.NodeType.ToString(), hop.ConfigJson,
                // hop.Before/hop.After deliberately NOT captured: those are the field's actual patient values.
                hop.Success, hop.Error, hop.DurationMs, hop.ExecutedAtUtc))
            .ToList();

        try
        {
            await _lineageCaptureDispatcher.EnqueueAsync(
                new LineageCaptureCommand(
                    context.WorkflowRunId, node.Id, resource.ResourceType, resource.ResourceId, entries,
                    Guid.NewGuid().ToString("N"))
                {
                    SourceSystemType = sourceSystem,
                    DestinationTypeName = destination.DestinationType.ToString(),
                    DestinationName = destination.Name,
                },
                cancellationToken);
        }
        catch (Exception)
        {
            // Intentionally swallowed — see the method summary.
        }
    }
}

public sealed class MappingNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly IJsonMappingEngine? _mappingEngine;
    private readonly IMappingMaterializer? _mappingMaterializer;
    private readonly IConfigurationRepository? _configurationRepository;
    private readonly IEffectiveRuleResolver? _ruleResolver;
    private readonly ITransformNodeRegistry? _transformNodeRegistry;
    private readonly ISystemSettingsCache? _settingsCache;
    private readonly IAppSecretAccessor? _secretAccessor;
    private readonly ILineageCaptureDispatcher? _lineageCaptureDispatcher;

    public MappingNodeExecutor(
        IJsonMappingEngine? mappingEngine = null,
        IMappingMaterializer? mappingMaterializer = null,
        IConfigurationRepository? configurationRepository = null,
        IEffectiveRuleResolver? ruleResolver = null,
        ITransformNodeRegistry? transformNodeRegistry = null,
        ISystemSettingsCache? settingsCache = null,
        IAppSecretAccessor? secretAccessor = null,
        ILineageCaptureDispatcher? lineageCaptureDispatcher = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, loggerFactory)
    {
        _mappingEngine = mappingEngine;
        _mappingMaterializer = mappingMaterializer;
        _configurationRepository = configurationRepository;
        _ruleResolver = ruleResolver;
        _transformNodeRegistry = transformNodeRegistry;
        _settingsCache = settingsCache;
        _secretAccessor = secretAccessor;
        _lineageCaptureDispatcher = lineageCaptureDispatcher;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        // FHIR-repository passthrough (e.g. Aidbox): the destination wizard's Step 3 "Send FHIR resources as-is"
        // choice (the default) is stamped onto this synthetic Field Mapping node's own config as dest_fhirMapMode —
        // see WorkflowGraphMapperService.syntheticMappingRequest(), which spreads the destination node's fields
        // (including dest_fhirMapMode and destinationTransformId) onto this node verbatim. A FHIR-repository
        // destination is spec-owned (docs/backend/14-mapping-profile-master-screen-plan.md) — it has no target
        // columns to map into, so every resource is emitted unchanged (or with customize rules applied, below)
        // rather than routed through the field-mapping engine, which requires a configured field list and would
        // otherwise silently emit zero records for any resource type nobody explicitly mapped.
        var isFhirDestination =
            string.Equals(ReadStringConfiguration(node, "destinationTransformId"), "dest-fhir", StringComparison.OrdinalIgnoreCase);
        var isFhirCustomize =
            isFhirDestination && string.Equals(ReadStringConfiguration(node, "dest_fhirMapMode"), "customize", StringComparison.OrdinalIgnoreCase);
        var isFhirPassthrough = isFhirDestination && !isFhirCustomize;

        if (isFhirPassthrough)
        {
            var passthroughRecords = new List<MappedDestinationRecord>();
            foreach (var resource in PassThroughNodeExecutor.ReadResourceEnvelopes(inputs))
            {
                var passthroughJson = Convert.ToString(resource.Payload) ?? "{}";
                passthroughRecords.Add(new MappedDestinationRecord(
                    context.WorkflowRunId,
                    resource.ResourceType,
                    resource.ResourceType,
                    resource.ResourceId,
                    new Dictionary<string, object?>(),
                    passthroughJson));
            }

            return new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                new MappedRecordBatch(passthroughRecords),
                WorkflowDataContract.MappedRecordBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["count"] = passthroughRecords.Count,
                    ["passthrough"] = true
                });
        }

        // "Customize fields before writing": apply the wizard's per-resource-type transform rules
        // (dest_fhirCustomRules — see FhirFieldTransformApplier/Aidbox-Customize-Transform-UX-Plan.md) to each
        // resource's raw JSON before emitting it unchanged otherwise. Each rule is failure-isolated — a bad rule
        // is recorded as a warning in this node's own output metadata rather than throwing or dropping the record.
        if (isFhirCustomize)
        {
            var rulesByResourceType = FhirCustomRulesParser.Parse(ReadStringConfiguration(node, "dest_fhirCustomRules"));
            var ruleErrors = new List<string>();
            var customizeRecords = new List<MappedDestinationRecord>();

            foreach (var resource in PassThroughNodeExecutor.ReadResourceEnvelopes(inputs))
            {
                var sourceJsonForCustomize = Convert.ToString(resource.Payload) ?? "{}";
                var rules = rulesByResourceType.TryGetValue(resource.ResourceType, out var resourceRules)
                    ? resourceRules
                    : Array.Empty<FhirCustomRule>();
                var transformedJson = FhirFieldTransformApplier.Apply(sourceJsonForCustomize, resource.ResourceType, rules, ruleErrors);

                customizeRecords.Add(new MappedDestinationRecord(
                    context.WorkflowRunId,
                    resource.ResourceType,
                    resource.ResourceType,
                    resource.ResourceId,
                    new Dictionary<string, object?>(),
                    transformedJson));
            }

            return new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                new MappedRecordBatch(customizeRecords),
                WorkflowDataContract.MappedRecordBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["count"] = customizeRecords.Count,
                    ["passthrough"] = true,
                    ["customize"] = true,
                    ["ruleErrors"] = ruleErrors
                });
        }

        var configuredFields = ReadConfiguration<IReadOnlyCollection<MappingFieldDto>>(node, "fields") ?? [];
        var configuredResourceType = ReadStringConfiguration(node, "resourceType") ?? "Patient";
        var configuredDestinationObject = ReadStringConfiguration(node, "destinationObject") ?? configuredResourceType;
        Guid.TryParse(ReadStringConfiguration(node, "sourceConnectionId"), out var sourceConnectionId);
        Guid.TryParse(ReadStringConfiguration(node, "destinationId"), out var destinationId);
        // The WORKFLOW's own id, stamped onto this node at save time — NOT context.WorkflowRunId, which used to
        // be passed here. A run id is freshly generated per execution, so it could never match the id a rule
        // was authored against: the resolver's Workflow tier was queried with a value guaranteed to miss, and
        // every lookup silently fell through to the tenant-wide tiers below it. Null on a workflow saved before
        // this key existed, which the resolver reads as "skip the Workflow tier" — exactly the old behaviour.
        Guid.TryParse(ReadStringConfiguration(node, "resourcePipelineRouteId"), out var routeId);
        var resourcePipelineRouteId = routeId == Guid.Empty ? (Guid?)null : routeId;

        // Resolved once per node execution (not per record) — both are stable for this whole batch, and the
        // transform-rule resolver only needs the type/system-type, never the full entities. The display names
        // are only for the lineage row (see LineageCaptureCommand's SourceConnectionName/DestinationName) —
        // the transform-rule resolver itself never sees them.
        var (destinationType, destinationName) = await ResolveDestinationTypeAsync(destinationId, cancellationToken);
        var (sourceSystem, sourceConnectionName) = await ResolveSourceSystemAsync(sourceConnectionId, cancellationToken);

        // Whole-resource FHIR destinations (Medplum, FHIR repository, Azure FHIR Service) persist the source
        // resource itself (MappedDestinationRecord.SourceJson), not a set of mapped relational columns — so
        // they legitimately have NO field mappings, and the "needs at least one mapped Value" gates below
        // (which exist to avoid writing bogus empty rows into a relational table) would otherwise drop every
        // resource, silently landing zero records. For these destinations we always emit one carrier record
        // per resource, carrying SourceJson.
        var wholeResourceFhir = destinationType is DestinationType.Medplum or DestinationType.FhirRepository or DestinationType.AzureFhirService;
        // Caches each field's resolved rule chain for the lifetime of this ExecuteAsync call — the same
        // (resourceType, destinationField, sourceField) combination recurs once per record in the batch, and
        // re-querying the resolver/repository for every single record would be wasted round trips for a rule
        // set that can't have changed mid-batch.
        var ruleCache = new Dictionary<string, IReadOnlyList<TransformationRule>>();

        // Each resource's PreMapping de-identification redactions (see WorkflowNodeOutputMetadataKeys.
        // PreMappingRedactions), keyed by resource id — attached by an upstream DeIdentification node so its
        // hops can be merged into the same Field Lineage chain this node builds for its own PostMapping rules.
        // Empty (not an error) whenever no upstream node produced any, or nothing was redacted.
        var preMappingRedactionsByResourceId = inputs
            .Select(input => input.Metadata?.TryGetValue(WorkflowNodeOutputMetadataKeys.PreMappingRedactions, out var value) == true
                ? value as IReadOnlyDictionary<string, IReadOnlyList<DeIdentificationFieldHop>>
                : null)
            .FirstOrDefault(value => value is not null);

        var records = new List<MappedDestinationRecord>();
        // One timestamp for the whole run so every row this node writes shares the same @now / WrittenOnUtc value.
        var runTimestampUtc = DateTime.UtcNow;
        // Resource types with no resolvable MappingProfile — surfaced in the output metadata below so "why is
        // my Encounter/Observation data missing" is answerable directly from execution history (this workflow's
        // resource type genuinely has no mapping configured for this destination) instead of needing to check
        // MappingProfiles by hand.
        var skippedResourceTypes = new List<string>();
        // Resources that WERE mapped (a field list resolved for their type) but produced no writable value at
        // all — see the per-resource check below for why an empty result has to be reported rather than dropped.
        var unmappedByResourceType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // A single Field Mapping node can receive a heterogeneous batch — e.g. an EpicSource node configured
        // for both Patient and Observation scopes feeds one Mapping node before a single Destination node.
        // Every resource type MUST be mapped through its OWN MappingProfile: applying the node's one resolved
        // profile to every envelope regardless of its real ResourceType isn't just "the other types go
        // unmapped" — their JSON still gets walked against whatever fields happen to exist (almost always just
        // the root "id"), silently producing bogus rows (e.g. an Observation's id landing in the Patient table
        // as a garbage "patient" row with every other column null — a real incident this grouping prevents).
        foreach (var group in PassThroughNodeExecutor.ReadResourceEnvelopes(inputs)
            .GroupBy(resource => resource.ResourceType, StringComparer.OrdinalIgnoreCase))
        {
            var resourceType = group.Key;
            var (fields, destinationObject) = await ResolveResourceMappingAsync(
                node, resourceType, configuredResourceType, configuredFields, configuredDestinationObject,
                cancellationToken);

            if (fields is null)
            {
                // A whole-resource FHIR destination writes the source resource verbatim and needs no MappingProfile,
                // so emit a carrier record (SourceJson only) per resource rather than skipping the type.
                if (wholeResourceFhir)
                {
                    foreach (var resource in group)
                    {
                        records.Add(new MappedDestinationRecord(
                            context.WorkflowRunId, resource.ResourceType, resourceType, resource.ResourceId,
                            new Dictionary<string, object?>(), Convert.ToString(resource.Payload) ?? "{}"));
                    }

                    continue;
                }

                // No profile exists for this resource type — nothing tells us how to map it, so skip it rather
                // than guess; guessing (reusing a different resource type's fields) is exactly the
                // silent-corruption bug this method guards against.
                //
                // Only surface it as a "skipped" omission when this workflow was actually configured to map
                // that type (it's this node's own default resourceType, or it has an entry in mappingProfileIds
                // whose profile turned out missing/deleted) — a real misconfiguration worth a "why is my
                // Encounter data missing" answer. A resource type with NO entry here was never part of this
                // workflow at all; it only showed up because an upstream fetch (e.g. an Epic Group export's
                // _type-omission workaround for a lone-Patient job) handed back more types than requested.
                // Flagging that as "skipped" would misread routine over-fetch as a broken mapping.
                if (ReadProfileIds(node).ContainsKey(resourceType) ||
                    string.Equals(resourceType, configuredResourceType, StringComparison.OrdinalIgnoreCase))
                {
                    skippedResourceTypes.Add(resourceType);
                }

                continue;
            }

            // Which source JsonPath fed each destination column — lets the transform-rule resolver prefer a
            // source-field-specific rule (see EffectiveRuleResolver.PreferSourceFieldSpecific) the same way it
            // already prefers a source-system-specific one, without the resolver needing to re-derive it itself.
            var sourceFieldByTarget = fields
                .GroupBy(f => f.TargetField, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().JsonPath, StringComparer.OrdinalIgnoreCase);

            // Warms CachingTerminologyLookupService for every distinct code this group is about to look up,
            // concurrently, before the per-resource pass below runs each one serially interleaved with the rest
            // of that resource's mapping work. Pure performance optimization — see the method's own doc comment
            // for why it can never change what value actually gets written.
            if (destinationType is not null)
            {
                await PreWarmCodeableConceptLookupsAsync(
                    group, fields, sourceFieldByTarget, resourceType, destinationType.Value, sourceSystem,
                    resourcePipelineRouteId, ruleCache, cancellationToken);

                fields = await MarkTransformTypedFieldsAsync(
                    fields, sourceFieldByTarget, resourceType, destinationType.Value, sourceSystem,
                    resourcePipelineRouteId, ruleCache, cancellationToken);
            }

            // Pipeline/runtime values a @token field can draw from (audit/lineage columns not present in the
            // source FHIR document): the run id, a shared write timestamp, and this group's resource type/
            // destination. Built once per group (everything here is constant across every resource IN this
            // group — including "@resourceType", since the outer GroupBy already guarantees every resource's
            // own ResourceType equals this group's key) and reused/mutated per resource below, rather than
            // reallocated on every iteration: the mapping engine only ever reads it synchronously within its
            // own Map() call, so nothing holds onto a stale snapshot between resources.
            var systemValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["@runId"] = context.WorkflowRunId,
                ["@now"] = runTimestampUtc,
                ["@resourceType"] = resourceType,
                ["@destinationObject"] = destinationObject,
                // @mappingProfileName/@mappingProfileId/@sourceConnectionId/@triggeredBy have no equivalent
                // here — the Runtime Plane's node config is inline JSON, not a Domain MappingProfile entity,
                // and this executor has no trigger identity to report. Left unset rather than a misleading
                // placeholder; a field using one of them resolves to null here exactly like an unmapped
                // token, same as JsonMappingEngine already does for any token absent from systemValues.
            };

            foreach (var resource in group)
            {
                var sourceJson = Convert.ToString(resource.Payload) ?? "{}";
                var preMappingHops = preMappingRedactionsByResourceId is not null
                    && preMappingRedactionsByResourceId.TryGetValue(resource.ResourceId, out var resourceHops)
                        ? resourceHops
                        : (IReadOnlyList<DeIdentificationFieldHop>)[];
                systemValues["@sourceResourceId"] = resource.ResourceId;
                // Freshly generated per resource (unlike every other token above) — mirrors
                // ConfiguredPipelineService's identical Configured Pipeline handling of this token.
                systemValues["@newGuid"] = Guid.NewGuid();
                var mapped = _mappingEngine?.Map(sourceJson, fields, systemValues);
                if (mapped is null)
                {
                    records.Add(new MappedDestinationRecord(
                        context.WorkflowRunId, resource.ResourceType, destinationObject, resource.ResourceId,
                        new Dictionary<string, object?>(), sourceJson));
                    continue;
                }

                // ArrayPolicy.SeparateDestination child-table rows travel as MappedChildTableRecords attached to
                // the parent row (not as separate flat records with their own DestinationObject) — the writer
                // resolves a single target table per write call, so a flat child record would otherwise get
                // written straight into the PARENT's table ("Invalid column name" for every child-only column).
                var childTables = BuildChildTableRecords(mapped.ChildTables, fields, resourceType);
                var referenceLookups = mapped.ReferenceLookups is { Count: > 0 }
                    ? mapped.ReferenceLookups
                        .Select(l => new MappedReferenceLookup(l.TargetField, l.LookupTable, l.LookupKeyColumn, l.ReferenceId))
                        .ToArray()
                    : null;

                // Parent row (Scalar/FirstItem/RejectIfMultiple/RepeatParent fields land here). Skipped only
                // when there's truly nothing to write — no parent-level field AND no child table either. A node
                // whose fields are entirely SeparateDestination still needs a parent row emitted (even with
                // empty Values) because the writer captures the child rows' FK value off THAT row's own write
                // (OUTPUT INSERTED) — dropping it would silently lose the child data instead of just writing an
                // extra near-empty row.
                if (mapped.Values.Count == 0 && childTables is null && !wholeResourceFhir)
                {
                    // Every mapped field came back empty for this resource, so there is no row to write. Counted
                    // (not silently dropped) because a WHOLE batch landing here is indistinguishable at the run
                    // level from "nothing to do" — the node emits zero records, the destination is handed nothing
                    // to reject, and the run reports Succeeded having written nothing at all. That is precisely
                    // how a node whose inline mapping failed to deserialize looked green while doing nothing.
                    unmappedByResourceType[resourceType] = unmappedByResourceType.GetValueOrDefault(resourceType) + 1;
                }

                if (mapped.Values.Count > 0 || childTables is not null || wholeResourceFhir)
                {
                    var dataset = _mappingMaterializer?.Materialize(destinationObject, mapped);
                    foreach (var parentRow in dataset?.ParentRows ?? [mapped.Values])
                    {
                        var (transformedRow, fhirWriteBackPatches, lineageEntries) = await ApplyTransformRulesAsync(
                            parentRow, resourceType, sourceFieldByTarget, destinationType, sourceSystem,
                            resourcePipelineRouteId, ruleCache, resource.ResourceId, sourceJson, preMappingHops,
                            mapped.RawArrayValues, cancellationToken);
                        var patchedSourceJson = fhirWriteBackPatches is { Count: > 0 }
                            ? FhirSourceJsonPatcher.ApplyPatches(sourceJson, fhirWriteBackPatches)
                            : sourceJson;

                        if (_lineageCaptureDispatcher is not null && lineageEntries is { Count: > 0 })
                        {
                            // Fire-and-continue: EnqueueAsync only ever writes to a channel/publishes to a
                            // broker — it never waits on the actual FieldLineageEntries insert, which happens
                            // out-of-band in the Worker (see LineageCaptureProcessor). A publish failure here
                            // must never fail the resource's own transform/write, so it's swallowed, not awaited
                            // into the caller's exception path.
                            try
                            {
                                await _lineageCaptureDispatcher.EnqueueAsync(
                                    new LineageCaptureCommand(
                                        context.WorkflowRunId,
                                        node.Id,
                                        resource.ResourceType,
                                        resource.ResourceId,
                                        lineageEntries,
                                        Guid.NewGuid().ToString("N"))
                                    {
                                        SourceSystemType = sourceSystem,
                                        SourceConnectionName = sourceConnectionName,
                                        DestinationTypeName = destinationType?.ToString(),
                                        DestinationName = destinationName,
                                    },
                                    cancellationToken);
                            }
                            catch (Exception) when (!cancellationToken.IsCancellationRequested)
                            {
                                // Lineage is diagnostic/audit data, not correctness-critical — losing a batch of
                                // it must never take down the pipeline run that produced it.
                            }
                        }

                        records.Add(new MappedDestinationRecord(
                            context.WorkflowRunId, resource.ResourceType, destinationObject, resource.ResourceId,
                            transformedRow, patchedSourceJson, childTables, referenceLookups));
                    }
                }
            }
        }

        // The record count entering the destination stage. Comparing this against the source stage's
        // ResourceTypeExtracted counts is how a silent drop in mapping is found — previously only possible by
        // decoding node metadata after the fact.
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
            Logger,
            FHIRBridge.Observability.Logging.LogEvents.TransformCompleted,
            "Mapping produced {RecordCount} destination record(s) across {ResourceTypeCount} resource type(s): [{ResourceTypeCounts}]",
            records.Count,
            records.Select(record => record.ResourceType).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            string.Join(", ", records
                .GroupBy(record => record.ResourceType, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key}={group.Count()}")));

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new MappedRecordBatch(records),
            WorkflowDataContract.MappedRecordBatch,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["count"] = records.Count,
                // Per-resource-type breakdown of "count" above (records this resource type actually contributed,
                // including child-table carrier rows) — the mapping-side counterpart to EpicSourceNode's own
                // "resourceTypeCounts", so a drop between the two is visible without decrypting anything.
                ["resourceTypeCounts"] = records
                    .GroupBy(record => record.ResourceType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
                // Resource types present in the upstream batch that had no resolvable MappingProfile for this
                // node's (sourceConnectionId, destinationId) — every one of their records was skipped entirely.
                // Reported through the same key the orchestrator already aggregates into
                // WorkflowRunStatus.PartialSuccess (see RankedWorkflowOrchestrator), so a mapping that yields
                // nothing is surfaced exactly like a destination write that rejects everything, instead of
                // passing as a clean success.
                ["skippedResourceTypes"] = BuildMappingOmissions(skippedResourceTypes, unmappedByResourceType)
            });
    }

    /// <summary>
    /// Flags every field whose rule chain declares its own output type, so <c>JsonMappingEngine</c> extracts
    /// that field's value WITHOUT coercing it to <c>MappingField.ValueType</c>.
    ///
    /// The mapping row's ValueType describes what reaches the COLUMN, which for a rule-backed field is the
    /// rule's output (an int age), not what sits in the source document (a birthDate string) — but extraction
    /// runs before the rules do, so coercing there attempts a conversion that cannot succeed and logs a
    /// mapping error for a field that is in fact mapped correctly. Reads through the same per-execution
    /// <paramref name="ruleCache"/> the real transform pass uses, so this costs no extra resolution.
    ///
    /// Deliberately keyed off the rule as resolved RIGHT NOW rather than anything stored on the mapping
    /// profile: rules live in their own table with their own lifecycle, so a rule added, disabled, reordered or
    /// deleted after the mapping was last saved takes effect on the very next run instead of leaving the
    /// profile's stamped ValueType to silently mis-describe the field.
    /// </summary>
    private async Task<IReadOnlyCollection<MappingFieldDto>> MarkTransformTypedFieldsAsync(
        IReadOnlyCollection<MappingFieldDto> fields,
        IReadOnlyDictionary<string, string> sourceFieldByTarget,
        string resourceType,
        DestinationType destinationType,
        string? sourceSystem,
        Guid? resourcePipelineRouteId,
        Dictionary<string, IReadOnlyList<TransformationRule>> ruleCache,
        CancellationToken cancellationToken)
    {
        if (_ruleResolver is null)
        {
            return fields;
        }

        var typedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetField in fields.Select(f => f.TargetField).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sourceFieldByTarget.TryGetValue(targetField, out var sourceField);
            var cacheKey = $"{resourceType}|{targetField}|{sourceField}";
            if (!ruleCache.TryGetValue(cacheKey, out var rules))
            {
                rules = await _ruleResolver.ResolveAsync(
                    destinationType, resourceType, targetField, resourcePipelineRouteId, sourceSystem,
                    RuleSourceFieldFormat.FromJsonPath(resourceType, sourceField), cancellationToken,
                    workflowScopedOnly: resourcePipelineRouteId is not null);
                ruleCache[cacheKey] = rules;
            }

            if (rules.Any(rule => rule.ExpectedValueType is not null))
            {
                typedTargets.Add(targetField);
            }
        }

        return typedTargets.Count == 0
            ? fields
            : fields
                .Select(f => typedTargets.Contains(f.TargetField) ? f with { DeferTypeToTransform = true } : f)
                .ToList();
    }


    /// <summary>
    /// Resolves every field in this resource-type group that has a <see cref="TransformNodeType.CodeableConceptBuilder"/>
    /// rule configured to look up its display text from the terminology DB, collects the distinct codes those
    /// fields actually carry across the whole group, and issues all of those lookups concurrently — so
    /// <see cref="Terminology.CachingTerminologyLookupService"/> (wherever it's wired in as
    /// <c>ITerminologyLookupService</c>) is already warm by the time the real per-resource pass below reaches
    /// each one, instead of every distinct code paying its network round trip serially, interleaved with the
    /// rest of that resource's mapping work. Never changes what value ends up written anywhere: it calls the
    /// exact same node with the exact same config the real pass would, so a cache write here is byte-identical
    /// to the one the real pass would have produced on its own — this only changes WHEN and how concurrently
    /// those network calls happen. Best-effort: any failure here is swallowed, since the real per-record pass
    /// remains the correctness path and will simply pay the normal (uncached) cost for whichever codes didn't
    /// warm successfully.
    /// </summary>
    private async Task PreWarmCodeableConceptLookupsAsync(
        IEnumerable<ResourceEnvelope> group,
        IReadOnlyCollection<MappingFieldDto> fields,
        IReadOnlyDictionary<string, string> sourceFieldByTarget,
        string resourceType,
        DestinationType destinationType,
        string? sourceSystem,
        Guid? resourcePipelineRouteId,
        Dictionary<string, IReadOnlyList<TransformationRule>> ruleCache,
        CancellationToken cancellationToken)
    {
        if (_mappingEngine is null || _ruleResolver is null || _transformNodeRegistry is null)
        {
            return;
        }

        var codeableConceptConfigByTarget = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetField in fields.Select(f => f.TargetField).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sourceFieldByTarget.TryGetValue(targetField, out var sourceField);
            var cacheKey = $"{resourceType}|{targetField}|{sourceField}";
            if (!ruleCache.TryGetValue(cacheKey, out var rules))
            {
                rules = await _ruleResolver.ResolveAsync(
                    destinationType, resourceType, targetField, resourcePipelineRouteId, sourceSystem,
                    RuleSourceFieldFormat.FromJsonPath(resourceType, sourceField), cancellationToken,
                    workflowScopedOnly: resourcePipelineRouteId is not null);
                ruleCache[cacheKey] = rules;
            }

            var codeableConceptRule = rules.FirstOrDefault(rule => rule.NodeType == TransformNodeType.CodeableConceptBuilder);
            if (codeableConceptRule is null)
            {
                continue;
            }

            Dictionary<string, string> config;
            try
            {
                config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(codeableConceptRule.ConfigJson) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            // Nothing to warm — a hand-typed display always wins outright, and lookup being explicitly disabled
            // means the real pass never calls the terminology service for this field either. Mirrors
            // CodeableConceptBuilderNode.ExecuteAsync's own reads of these same two config keys.
            var resolveDisplayFromTerminology = !config.TryGetValue("resolveDisplayFromTerminology", out var resolveFlag)
                || !bool.TryParse(resolveFlag, out var resolveFlagParsed)
                || resolveFlagParsed;
            var hasHandTypedDisplay = config.TryGetValue("display", out var handTypedDisplay) && !string.IsNullOrWhiteSpace(handTypedDisplay);
            if (!resolveDisplayFromTerminology || hasHandTypedDisplay)
            {
                continue;
            }

            codeableConceptConfigByTarget[targetField] = config;
        }

        if (codeableConceptConfigByTarget.Count == 0)
        {
            return;
        }

        var distinctCodesByTarget = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in group)
        {
            var sourceJson = Convert.ToString(resource.Payload) ?? "{}";
            var mapped = _mappingEngine.Map(sourceJson, fields, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
            if (mapped is null)
            {
                continue;
            }

            foreach (var targetField in codeableConceptConfigByTarget.Keys)
            {
                if (!mapped.Values.TryGetValue(targetField, out var rawValue) ||
                    rawValue?.ToString() is not { Length: > 0 } code)
                {
                    continue;
                }

                if (!distinctCodesByTarget.TryGetValue(targetField, out var codes))
                {
                    codes = new HashSet<string>(StringComparer.Ordinal);
                    distinctCodesByTarget[targetField] = codes;
                }

                codes.Add(code);
            }
        }

        var codeableConceptBuilderNode = _transformNodeRegistry.Get(TransformNodeType.CodeableConceptBuilder);
        var warmTasks = distinctCodesByTarget
            .SelectMany(entry => entry.Value.Select(code =>
                codeableConceptBuilderNode.ExecuteAsync(code, codeableConceptConfigByTarget[entry.Key], secret: null, cancellationToken)))
            .ToList();

        if (warmTasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(warmTasks);
        }
        catch
        {
            // Best-effort only — see the method's doc comment. The real per-record pass below is the
            // correctness path and will retry (uncached) whichever codes failed to warm here.
        }
    }

    /// <summary>
    /// Resolves one resource type's fields + destination object.
    ///
    /// <para><b>Self-contained first</b> (plan §3.3): a node carrying its own inline mapping for this resource
    /// type is authoritative and is used without touching the repository. That is the whole point of the
    /// self-contained model — what ran is what the node says, so a run is reproducible and editing a shared
    /// master cannot silently change this workflow.</para>
    ///
    /// <para><b>Then by id</b>, for nodes not yet migrated: <c>mappingProfileIds</c> — a JSON object of
    /// <c>{resourceType: mappingProfileId}</c> the build endpoint stamps onto this exact node — then the legacy
    /// single <c>mappingProfileId</c> for the node's own configured resource type. Both are ids THIS node saved,
    /// so neither can pick up a different workflow's profile.</para>
    ///
    /// Deliberately does NOT fall back to searching MappingProfile by the natural key (resourceType,
    /// sourceConnectionId, destinationId): that triple is shared by any workflow built on the same source
    /// connection + destination + resource type, so a search-based fallback would silently resolve to (and,
    /// once profiles diverge, keep flapping onto) a DIFFERENT workflow's profile — the exact "Invalid column
    /// name" incident this replaces. Returns null Fields when nothing resolves — the caller skips that resource
    /// type entirely rather than guessing with another resource type's shape or another workflow's profile.
    /// </summary>
    private async Task<(IReadOnlyCollection<MappingFieldDto>? Fields, string DestinationObject)> ResolveResourceMappingAsync(
        WorkflowNode node,
        string resourceType,
        string configuredResourceType,
        IReadOnlyCollection<MappingFieldDto> configuredFields,
        string configuredDestinationObject,
        CancellationToken cancellationToken)
    {
        // Inline mapping for this exact resource type wins outright — no lookup, nothing to go stale.
        if (TryReadInlineResourceMapping(node, resourceType) is { } inline)
        {
            return (inline.Fields, inline.DestinationObject ?? configuredDestinationObject);
        }

        // The node's own single-resource config counts as inline too, when it names this resource type.
        if (configuredFields.Count > 0
            && string.Equals(resourceType, configuredResourceType, StringComparison.OrdinalIgnoreCase))
        {
            return (configuredFields, configuredDestinationObject);
        }

        if (_configurationRepository is not null && ReadProfileIds(node).TryGetValue(resourceType, out var profileId))
        {
            var profile = await _configurationRepository.GetMappingProfileAsync(profileId, cancellationToken);
            if (profile is not null)
            {
                return (profile.Fields.Select(ConfigurationMapper.ToDto).Where(f => f.IsEnabled).ToArray(), profile.DestinationObject);
            }
        }

        if (string.Equals(resourceType, configuredResourceType, StringComparison.OrdinalIgnoreCase))
        {
            // No repository (e.g. unit tests) or nothing resolvable at all for the node's own configured
            // resource type — fall back to whatever fields were embedded directly on the node's config.
            return (configuredFields, configuredDestinationObject);
        }

        return (null, configuredDestinationObject);
    }

    /// <summary>
    /// The omissions this mapping node needs to report, in the shape the orchestrator aggregates into
    /// <c>WorkflowRunStatus.PartialSuccess</c>: resource types with no resolvable mapping at all, plus resource
    /// types that WERE mapped but whose records all came out empty.
    ///
    /// The second case used to be invisible. A resource that maps to no values is not written (there is no row to
    /// write), so with an entire batch in that state the node emits zero records, the destination writer is handed
    /// nothing and therefore rejects nothing, and every node plus the run itself reports Succeeded — a green run
    /// that moved no data. Returns null when there is nothing to report, matching the key's existing contract.
    /// </summary>
    private static string[]? BuildMappingOmissions(
        IReadOnlyCollection<string> skippedResourceTypes,
        IReadOnlyDictionary<string, int> unmappedByResourceType)
    {
        var reasons = new List<string>(skippedResourceTypes.Distinct(StringComparer.OrdinalIgnoreCase));

        foreach (var entry in unmappedByResourceType.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add(
                $"{entry.Key}: {entry.Value} record(s) produced no mapped values and were not written "
                + "(check this resource type's field mappings)");
        }

        return reasons.Count > 0 ? reasons.ToArray() : null;
    }

    /// <summary>
    /// This node's own inline mapping for one resource type, from the self-contained <c>mappings</c> block
    /// (plan §3.3): <c>{"mappings": {"Patient": {"destinationObject": "dbo.Patient", "fields": [ … ]}}}</c>.
    /// Null when the node carries no such block, or none for this resource type — the caller then falls back to
    /// the id-based lookups for nodes that have not been migrated yet.
    /// </summary>
    private static (IReadOnlyCollection<MappingFieldDto> Fields, string? DestinationObject)? TryReadInlineResourceMapping(
        WorkflowNode node,
        string resourceType)
    {
        var mappings = ReadConfiguration<Dictionary<string, InlineResourceMapping>>(node, "mappings");
        if (mappings is null || mappings.Count == 0)
        {
            return null;
        }

        // Resource types are case-insensitive everywhere else in the engine; the JSON dictionary is not.
        var match = mappings.FirstOrDefault(entry =>
            string.Equals(entry.Key, resourceType, StringComparison.OrdinalIgnoreCase));

        if (match.Value?.Fields is not { Count: > 0 } fields)
        {
            return null;
        }

        return (fields.Where(field => field.IsEnabled).ToArray(), match.Value.DestinationObject);
    }

    /// <summary>One resource type's self-contained mapping, as stored on the node.</summary>
    private sealed class InlineResourceMapping
    {
        public string? DestinationObject { get; set; }

        public List<MappingFieldDto>? Fields { get; set; }
    }

    private async Task<(DestinationType? Type, string? Name)> ResolveDestinationTypeAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        if (_configurationRepository is null || destinationId == Guid.Empty)
        {
            return (null, null);
        }

        var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
        return (destination?.DestinationType, destination?.Name);
    }

    private async Task<(string? SystemType, string? Name)> ResolveSourceSystemAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        if (_configurationRepository is null || sourceConnectionId == Guid.Empty)
        {
            return (null, null);
        }

        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        return (sourceConnection?.SourceSystemType.ToString(), sourceConnection?.Name);
    }


    /// <summary>
    /// Runs every already-mapped value in <paramref name="row"/> through whichever transform-rule chain
    /// currently applies to its destination field (Workflow → Field → ResourceType → DestinationType → Global,
    /// via <see cref="IEffectiveRuleResolver"/>) — the same resolve-then-apply logic
    /// <c>TransformationRuleService.PreviewAsync</c> uses, inlined here with a per-execution cache so a batch
    /// of many records only resolves each field's rule chain once. A no-op (returns <paramref name="row"/>
    /// unchanged) when the rule engine's dependencies weren't supplied or no destination type could be
    /// resolved — existing pipelines with no rules configured, or running without this optional wiring, are
    /// completely unaffected.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, object?> Values, IReadOnlyList<(string Path, object? Value)>? FhirWriteBackPatches, IReadOnlyList<LineageHopEntryDto>? LineageEntries)> ApplyTransformRulesAsync(
        IReadOnlyDictionary<string, object?> row,
        string resourceType,
        IReadOnlyDictionary<string, string> sourceFieldByTarget,
        DestinationType? destinationType,
        string? sourceSystem,
        Guid? resourcePipelineRouteId,
        Dictionary<string, IReadOnlyList<TransformationRule>> ruleCache,
        string resourceId,
        string? sourceJson,
        IReadOnlyList<DeIdentificationFieldHop> preMappingHops,
        IReadOnlyDictionary<string, IReadOnlyList<object?>>? rawArrayValues,
        CancellationToken cancellationToken)
    {
        if (_ruleResolver is null || _transformNodeRegistry is null || destinationType is null || row.Count == 0)
        {
            return (row, null, null);
        }

        Dictionary<string, object?>? transformed = null;
        // Only populated for a FhirRepository-typed destination, and only when a rule in the field's chain sets
        // FhirWriteBackJsonPath — see TransformationRule.FhirWriteBackJsonPath's doc comment. Applied by the
        // caller against this resource's own SourceJson so an Aidbox/Medplum-style destination receives the
        // transformed value too, not just the flat Values a SQL/Csv/Mongo destination reads.
        List<(string Path, object? Value)>? fhirWriteBackPatches = null;
        // Buffered in-memory only — see LineageCaptureCommand's doc comment for why this never touches the DB
        // directly. Null (not an empty list) whenever there's no dispatcher wired up, so a pipeline with lineage
        // capture disabled pays zero allocation cost for it.
        List<LineageHopEntryDto>? lineageEntries = _lineageCaptureDispatcher is null ? null : [];

        foreach (var (destinationField, value) in row)
        {
            sourceFieldByTarget.TryGetValue(destinationField, out var sourceField);
            var cacheKey = $"{resourceType}|{destinationField}|{sourceField}";

            // PreMapping de-identification hops for this field, if any — prepended ahead of whatever runs below
            // so the field's chain reads source-order: redaction first, then the PostMapping rule chain (or the
            // plain pass-through hop). Continues the same NodeOrder sequence so the whole chain stays contiguous.
            var nodeOrder = 0;
            if (preMappingHops.Count > 0 && sourceField is not null)
            {
                foreach (var hop in preMappingHops)
                {
                    if (!string.Equals(hop.SourceField, sourceField, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    lineageEntries?.Add(new LineageHopEntryDto(
                        destinationField,
                        sourceField,
                        nodeOrder++,
                        "DeIdentification:" + hop.Strategy,
                        hop.ConfigJson,
                        // Before/After values deliberately NOT captured: they are the field's actual patient data.
                        hop.Success,
                        hop.ErrorMessage,
                        null,
                        DateTimeOffset.UtcNow));
                }
            }

            if (!ruleCache.TryGetValue(cacheKey, out var rules))
            {
                rules = await _ruleResolver.ResolveAsync(
                    destinationType.Value, resourceType, destinationField, resourcePipelineRouteId, sourceSystem,
                    RuleSourceFieldFormat.FromJsonPath(resourceType, sourceField), cancellationToken,
                    // A node carrying a workflow id was authored by the V2 builder, whose rules are
                    // pipeline-private — so it must not inherit another workflow's. A V1 graph never carries
                    // one, which leaves its five-tier resolution exactly as it was.
                    workflowScopedOnly: resourcePipelineRouteId is not null);
                ruleCache[cacheKey] = rules;
            }

            if (rules.Count == 0)
            {
                // No PostMapping rule chain for this field — still record a pass-through hop so every field the
                // Mapping node actually writes has at least one lineage row (previously this `continue` meant a
                // plain "Direct/write verbatim" mapping, the common case, never got any lineage at all).
                lineageEntries?.Add(new LineageHopEntryDto(
                    destinationField,
                    sourceField,
                    nodeOrder,
                    "DirectMapping",
                    "{}",
                    // Before/After values deliberately NOT captured: they are the field's actual patient data.
                    true,
                    null,
                    null,
                    DateTimeOffset.UtcNow));
                continue;
            }

            // A rule chain LED by ConcatenationTemplating/ArrayListOperations exists specifically to
            // operate on every occurrence of a repeating field, not the single value ArrayPolicy already
            // collapsed `value` down to (see MappingTestResultDto.RawArrayValues's doc comment) — hand it
            // the real array instead, so its own operation/template/separator config becomes the actual
            // authority over which/how many items are used (superseding the field's Instance Selection,
            // which chose that single collapsed value in the first place).
            var currentValue =
                rules[0].NodeType is TransformNodeType.ConcatenationTemplating or TransformNodeType.ArrayListOperations
                && rawArrayValues is not null
                && rawArrayValues.TryGetValue(destinationField, out var rawItems)
                && rawItems.Count > 1
                    ? rawItems
                    : value;
            string? writeBackPath = null;
            foreach (var rule in rules)
            {
                var hopIndex = nodeOrder++;
                if (!string.IsNullOrWhiteSpace(rule.FhirWriteBackJsonPath))
                {
                    writeBackPath = rule.FhirWriteBackJsonPath;
                }

                if (TransformNullPolicy.ShouldShortCircuit(rule, currentValue))
                {
                    currentValue = TransformNullPolicy.Apply(rule, currentValue, out var stopChain);
                    if (stopChain)
                    {
                        break;
                    }

                    continue;
                }

                var node = _transformNodeRegistry.Get(rule.NodeType);
                Dictionary<string, string> config;
                try
                {
                    config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ConfigJson) ?? [];
                }
                catch (System.Text.Json.JsonException)
                {
                    // A corrupted ConfigJson is a data-integrity problem with the rule row itself, not this
                    // record — skip this one rule rather than let a JSON parse error take down the whole batch.
                    continue;
                }

                string? secret = null;
                if (rule.NodeType is TransformNodeType.HashingMasking or TransformNodeType.DateMathAge)
                {
                    secret = _secretAccessor?.TransformHashingKey;
                }

                // Reserved, caller-populated keys (never persisted). DateMathAge's per-patient seeded shift
                // needs "the patient this row belongs to," which for a Patient-resource row is simply its own
                // id; other resource types (Observation, Encounter, ...) would need reference resolution this
                // generic loop doesn't have, so they fall back to DateMathAge's fixed `days` config instead.
                config[ReservedTransformConfigKeys.DestinationType] = destinationType.Value.ToString();
                if (rule.NodeType == TransformNodeType.DateMathAge && string.Equals(resourceType, "Patient", StringComparison.OrdinalIgnoreCase))
                {
                    config[ReservedTransformConfigKeys.PatientId] = resourceId;
                }

                if (rule.NodeType == TransformNodeType.CodeableConceptBuilder)
                {
                    var siblingDisplay = FhirSourceJsonPatcher.TryReadSiblingDisplay(sourceJson, sourceField);
                    if (siblingDisplay is not null)
                    {
                        config[ReservedTransformConfigKeys.SourceDisplayHint] = siblingDisplay;
                    }
                }

                var hopInput = currentValue;
                var hopExecutedAtUtc = DateTimeOffset.UtcNow;
                var hopStopwatch = lineageEntries is null ? null : Stopwatch.StartNew();
                var result = await TransformNodeApplier.ExecuteWithArrayModeAsync(node, currentValue, config, secret, rule.ArrayMode, cancellationToken);
                hopStopwatch?.Stop();

                lineageEntries?.Add(new LineageHopEntryDto(
                    destinationField,
                    sourceField,
                    hopIndex,
                    rule.NodeType.ToString(),
                    result.ResolvedSystemOverride is null
                        ? rule.ConfigJson
                        : WithResolvedSystemOverride(rule.ConfigJson, result.ResolvedSystemOverride),
                    // Before/After values deliberately NOT captured: they are the field's actual patient data.
                    result.Success,
                    result.Success ? null : result.Error,
                    hopStopwatch?.Elapsed.TotalMilliseconds,
                    hopExecutedAtUtc));

                if (result.Success)
                {
                    currentValue = result.Value;
                    continue;
                }

                currentValue = rule.ErrorPolicy switch
                {
                    TransformErrorPolicy.PassThrough => currentValue,
                    _ => null
                };

                if (rule.ErrorPolicy is TransformErrorPolicy.Fail or TransformErrorPolicy.RouteToDeadLetter)
                {
                    break;
                }
            }

            if (writeBackPath is not null && destinationType.Value == DestinationType.FhirRepository)
            {
                fhirWriteBackPatches ??= [];
                fhirWriteBackPatches.Add((writeBackPath, currentValue));
            }

            transformed ??= new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
            // A FHIR complex-type builder node (HumanNameParsing, AddressParsing, TelecomNormalization,
            // IdentifierFormatting, ReferenceConstruction) yields a System.Text.Json.Nodes.JsonObject/JsonNode —
            // see the doc comment on ITransformNode.TransformResult. A destination writer's ADO.NET parameter
            // binding has no mapping for that CLR type (MappedSqlServerDestinationWriter throws "No mapping
            // exists from object type ... JsonObject"), so it must be serialized to its JSON text here, the same
            // way JsonMappingEngine's own MappingValueType.Json case stores a JSON column as a string.
            transformed[destinationField] = currentValue is System.Text.Json.Nodes.JsonNode jsonNode
                ? jsonNode.ToJsonString()
                : CoerceToExpectedValueType(
                    currentValue,
                    rules.LastOrDefault(r => r.ExpectedValueType is not null)?.ExpectedValueType,
                    destinationType.Value);
        }

        // A configured field whose source path matched nothing in this resource never reaches `row` at all, so
        // the loop above cannot see it and it would otherwise leave NO trace anywhere: no value, no lineage row,
        // no error, and a run that still reports Succeeded while the destination column silently holds NULL.
        // That silence is what makes an addressing mistake (e.g. a jsonPath that lost its "[*]" and so no longer
        // matches an array-valued element) practically undiagnosable from the product — the only way to notice
        // was to query the destination and find the column empty. Record an explicit unsuccessful hop instead,
        // so "the field resolved to nothing" is visible in lineage exactly like any other failure.
        if (lineageEntries is not null)
        {
            foreach (var (destinationField, sourceField) in sourceFieldByTarget)
            {
                if (row.ContainsKey(destinationField))
                {
                    continue;
                }

                lineageEntries.Add(new LineageHopEntryDto(
                    destinationField,
                    sourceField,
                    0,
                    "SkippedNoMatch",
                    "{}",
                    // Before/After values deliberately NOT captured: they are the field's actual patient data.
                    false,
                    $"Source path '{sourceField}' matched no value in this {resourceType} — the destination column was left unwritten.",
                    null,
                    DateTimeOffset.UtcNow));
            }
        }

        return (transformed ?? row, fhirWriteBackPatches, lineageEntries);
    }

    /// <summary>
    /// A transform node's job is to produce a valid FHIR value — for most Date/DateTime/Integer/Decimal-declared
    /// fields that means a formatted STRING (e.g. DateTimeFormatNode always returns "2026-03-14", never a native
    /// DateTime), because that's what a FHIR-native destination needs. A relational destination needs the
    /// opposite: RelationalDestinationWriterBase.Stringify only avoids re-stringifying a value that already
    /// arrives as a native CLR DateTime/DateOnly/int/decimal/etc. (see its own doc comment) — a plain string gets
    /// sent as an untyped ADO `text` parameter, which PostgreSQL then refuses to implicitly cast back to the
    /// destination column's real `date`/`integer`/`numeric` type (42804), even though the string is
    /// well-formed. JsonMappingEngine.ConvertValue already solves exactly this for a field with NO transform
    /// rule (coercing straight to a native CLR type from ValueType); this mirrors that same coercion for a
    /// field whose value just came OUT of a rule chain, using the chain's own declared ExpectedValueType (see
    /// FieldMappingJoinPopoverComponent.resolveExpectedValueType and CreateMappingProfileRequestValidator on the
    /// portal side — same value, already validated to match the destination column at save time). Only ever
    /// narrows a string; every other CLR shape (already-native DateTime/int/decimal, null, JsonNode-turned-string
    /// from the branch above) passes through unchanged, so a node that already self-types (DateMathAge's "age"
    /// operation → int, BooleanConversion → bool) is untouched.
    ///
    /// Scoped to a relational destination only: MappedDestinationSerialization.ToMappedOnlyCsv/ToCsv format a
    /// value with a bare `.ToString()`, so coercing a Date/DateTime field to a native DateTime here would make a
    /// CSV/Blob export silently switch from DateTimeFormatNode's "2026-03-14" to .NET's culture-formatted
    /// "3/14/2026 12:00:00 AM" — a destination-format regression this coercion must never cause, since it exists
    /// solely to satisfy a relational engine's strict column typing.
    /// </summary>
    internal static object? CoerceToExpectedValueType(
        object? value, MappingValueType? expectedValueType, DestinationType destinationType)
    {
        if (value is not string text || expectedValueType is null || !IsRelationalDestination(destinationType))
        {
            return value;
        }

        return expectedValueType switch
        {
            // Parsed with the same "assume UTC if the string carries no offset, then adjust to it" pair used
            // below for DateTime — an offset-bearing input (e.g. a raw FHIR instant) must resolve to the same
            // calendar date regardless of the host machine's local time zone, not silently roll to the next/
            // previous day depending on where this process happens to be running.
            MappingValueType.Date => DateTime.TryParse(
                text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
                ? date.Date
                : value,
            // AssumeUniversal ALONE (Kind=Local) — deliberately NOT combined with AdjustToUniversal, to match
            // JsonMappingEngine.ConvertDate's own parsing exactly (the no-transform-rule path this mirrors).
            // Npgsql (no EnableLegacyTimestampBehavior in this repo) infers "timestamptz" from a Kind=Utc
            // DateTime and "timestamp" from Kind=Local/Unspecified; a destination column normalized from
            // "datetime2"/"datetime" is a plain "timestamp" (see PostgreSqlDdlTypeValidator), so a Kind=Utc value
            // here would trip the exact 42804 this coercion exists to prevent.
            MappingValueType.DateTime => DateTime.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime)
                ? dateTime
                : value,
            MappingValueType.Integer => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                ? integer
                : value,
            MappingValueType.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec)
                ? dec
                : value,
            MappingValueType.Boolean => bool.TryParse(text, out var boolean) ? boolean : value,
            _ => value,
        };
    }

    /// <summary>The relational engines RelationalDestinationWriterBase/MappedSqlServerDestinationWriter actually
    /// write to — mirrors SqlDestinationSchemaService.IsRelational (that class lives in the non-Runtime
    /// Infrastructure project, which this one doesn't reference).</summary>
    private static bool IsRelationalDestination(DestinationType destinationType) => destinationType is
        DestinationType.SqlServer or DestinationType.AzureSql or DestinationType.PostgreSql
        or DestinationType.MySql or DestinationType.DataFabricWarehouse;

    /// <summary>Best-effort JSON serialization of a hop's before/after value for lineage storage — a lineage
    /// record that fails to serialize a value (e.g. an unexpected CLR type) should still record the hop with
    /// that side blank, not throw and lose the whole entry.</summary>
    private static string? SerializeLineageValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is System.Text.Json.Nodes.JsonNode jsonNode)
        {
            return jsonNode.ToJsonString();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return value.ToString();
        }
    }

    /// <summary>Appends a "resolvedSystem" note to the rule's config JSON for THIS lineage hop only — the
    /// persisted <see cref="TransformationRule.ConfigJson"/> itself is never touched. Used when
    /// CodeableConceptBuilderNode's opt-in cross-system auto-detect finds a code under a different local
    /// system than the rule's own "system" setting, so the substitution is visible in Execution History
    /// instead of silently masking what the configured system actually was.</summary>
    private static string WithResolvedSystemOverride(string configJson, string resolvedSystem)
    {
        try
        {
            var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(configJson) ?? [];
            config["resolvedSystem"] = resolvedSystem;
            return System.Text.Json.JsonSerializer.Serialize(config);
        }
        catch (System.Text.Json.JsonException)
        {
            return configJson;
        }
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new MappedRecordBatch(inputs.Select(input => input.Payload!).Where(payload => payload is not null).ToArray());

    /// <summary>
    /// Resolves each child table's FK/parent-key column names from the (import-populated)
    /// <see cref="MappingFieldDto.ForeignKeyColumn"/>/<see cref="MappingFieldDto.ParentKeyColumn"/> metadata on
    /// one of its own fields. A child table with no field carrying that metadata can't be linked back to a
    /// parent row — skipped rather than written with a missing/garbage FK value (mirrors
    /// ConfiguredPipelineService's identical guard for the Configured Pipeline execution path).
    /// </summary>
    private static IReadOnlyList<MappedChildTableRecord>? BuildChildTableRecords(
        IReadOnlyList<MappingChildTableDto>? childTables, IReadOnlyCollection<MappingFieldDto> fields, string resourceType)
    {
        if (childTables is not { Count: > 0 })
        {
            return null;
        }

        var records = new List<MappedChildTableRecord>();
        foreach (var childTable in childTables)
        {
            var fkField = fields.FirstOrDefault(f =>
                string.Equals(f.DestinationObject, childTable.Name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(f.ForeignKeyColumn));

            if (fkField is null)
            {
                continue;
            }

            var rows = childTable.Rows
                .Select(row => (IReadOnlyDictionary<string, object?>)row
                    .Where(kv => !string.Equals(kv.Key, "RowIndex", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                .ToList();

            records.Add(new MappedChildTableRecord(childTable.Name, fkField.ForeignKeyColumn!, fkField.ParentKeyColumn ?? "Id", rows));
        }

        return records.Count > 0 ? records : null;
    }
}

public sealed class TerminologyNodeExecutor : PassThroughNodeExecutor
{
    private readonly IMappedRecordNormalizationService? _normalizationService;

    public TerminologyNodeExecutor(IMappedRecordNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.Terminology, WorkflowDataContract.MappedRecordBatch)
    {
        _normalizationService = normalizationService;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var fields = ReadConfiguration<IReadOnlyCollection<MappingFieldDto>>(node, "fields") ?? [];
        if (_normalizationService is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        var records = new List<MappedDestinationRecord>();
        foreach (var record in ReadMappedRecords(inputs))
        {
            records.Add(await _normalizationService.NormalizeAsync(
                new MappedRecordNormalizationRequest(record.SourceJson ?? "{}", record, fields),
                cancellationToken));
        }

        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
            Logger,
            FHIRBridge.Observability.Logging.LogEvents.TransformCompleted,
            "Terminology normalization applied to {RecordCount} record(s) across {FieldCount} configured field(s).",
            records.Count, fields.Count);

        return new WorkflowNodeOutput(node.Id, node.NodeType, new MappedRecordBatch(records), WorkflowDataContract.MappedRecordBatch);
    }
}

public sealed class TerminologyValidateNodeExecutor : PassThroughNodeExecutor
{
    public TerminologyValidateNodeExecutor(IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.TerminologyValidate, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class TerminologyLookupNodeExecutor : PassThroughNodeExecutor
{
    public TerminologyLookupNodeExecutor(IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.TerminologyLookup, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class TerminologyTranslateNodeExecutor : PassThroughNodeExecutor
{
    public TerminologyTranslateNodeExecutor(IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.TerminologyTranslate, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class TerminologyExpandNodeExecutor : PassThroughNodeExecutor
{
    public TerminologyExpandNodeExecutor(IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.TerminologyExpand, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class RepeatingArrayMappingNodeExecutor : PassThroughNodeExecutor
{
    public RepeatingArrayMappingNodeExecutor()
        : base(WorkflowNodeTypes.RepeatingArrayMapping, WorkflowDataContract.MappedRecordBatch)
    {
    }
}

public abstract class PassThroughNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly IResourceNormalizationService? _normalizationService;

    protected PassThroughNodeExecutor(
        string nodeType,
        WorkflowDataContract outputContract,
        IResourceNormalizationService? normalizationService = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(nodeType, outputContract, loggerFactory)
    {
        _normalizationService = normalizationService;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        if (_normalizationService is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        var normalized = new List<ResourceEnvelope>();
        foreach (var resource in ReadResourceEnvelopes(inputs))
        {
            var result = await _normalizationService.NormalizeAsync(
                new ResourceNormalizationRequest(
                    context.WorkflowRunId,
                    resource.ResourceType,
                    resource.ResourceId,
                    Convert.ToString(resource.Payload) ?? "{}"),
                cancellationToken);

            normalized.Add(resource with { Payload = result.NormalizedJson });
        }

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new NormalizedResourceBatch(normalized),
            OutputContract,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["count"] = normalized.Count
            });
    }

    protected override object? CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => inputs.Count == 1 ? inputs.Single().Payload : inputs.Select(input => input.Payload).ToArray();

    public static IReadOnlyCollection<ResourceEnvelope> ReadResourceEnvelopes(IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => inputs.SelectMany(input => input.Payload switch
            {
                ResourceBatch batch => batch.Resources,
                // NormalizedResourceBatch.Resources is IReadOnlyCollection<object>, and every producer
                // (PassThroughNodeExecutor — Normalization / DataQualityScoring / FlattenExtensions /
                // PatientMatching) fills it with ResourceEnvelope instances. Wrapping those in ANOTHER
                // envelope put the envelope object itself in the Payload slot, so every downstream
                // Convert.ToString(resource.Payload) yielded "ResourceEnvelope { ... }" instead of the
                // resource JSON — "'R' is an invalid start of a value" out of the mapping engine, and a
                // silently skipped de-identification pass before it. It also relabelled every resource as
                // "Patient" with its index as the id. Unwrap first, exactly as the DeIdentifiedBatch branch
                // below already does for the same object-typed collection.
                NormalizedResourceBatch batch => batch.Resources.Select((resource, index) => resource switch
                {
                    ResourceEnvelope envelope => envelope,
                    MappedDestinationRecord record => new ResourceEnvelope(record.ResourceType, record.SourceResourceId ?? index.ToString(), record.SourceJson ?? "{}"),
                    _ => new ResourceEnvelope("Patient", index.ToString(), resource)
                }),
                DeIdentifiedBatch batch => batch.Records.Select((resource, index) => resource switch
                {
                    ResourceEnvelope envelope => envelope,
                    MappedDestinationRecord record => new ResourceEnvelope(record.ResourceType, record.SourceResourceId ?? index.ToString(), record.SourceJson ?? "{}"),
                    _ => new ResourceEnvelope("Patient", index.ToString(), resource)
                }),
                ResourceEnvelope resource => [resource],
                _ => []
            })
            .ToArray();

    public static IReadOnlyCollection<MappedDestinationRecord> ReadMappedRecords(IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => inputs.SelectMany(input => input.Payload switch
            {
                MappedRecordBatch batch => batch.Records.OfType<MappedDestinationRecord>(),
                DeIdentifiedBatch batch => batch.Records.OfType<MappedDestinationRecord>(),
                MappedDestinationRecord record => [record],
                _ => []
            })
            .ToArray();
}
