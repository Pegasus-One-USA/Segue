using System.Diagnostics;
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
        ILineageCaptureDispatcher? lineageCaptureDispatcher = null)
        : base(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch)
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
                    context.WorkflowRunId, ruleCache, cancellationToken);
            }

            foreach (var resource in group)
            {
                var sourceJson = Convert.ToString(resource.Payload) ?? "{}";
                var preMappingHops = preMappingRedactionsByResourceId is not null
                    && preMappingRedactionsByResourceId.TryGetValue(resource.ResourceId, out var resourceHops)
                        ? resourceHops
                        : (IReadOnlyList<DeIdentificationFieldHop>)[];
                // Pipeline/runtime values a @token field can draw from (audit/lineage columns not present in the
                // source FHIR document): the run id, a shared write timestamp, and the resource's own type/id.
                var systemValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["@runId"] = context.WorkflowRunId,
                    ["@now"] = runTimestampUtc,
                    ["@resourceType"] = resource.ResourceType,
                    ["@sourceResourceId"] = resource.ResourceId,
                };
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
                if (mapped.Values.Count > 0 || childTables is not null || wholeResourceFhir)
                {
                    var dataset = _mappingMaterializer?.Materialize(destinationObject, mapped);
                    foreach (var parentRow in dataset?.ParentRows ?? [mapped.Values])
                    {
                        var (transformedRow, fhirWriteBackPatches, lineageEntries) = await ApplyTransformRulesAsync(
                            parentRow, resourceType, sourceFieldByTarget, destinationType, sourceSystem,
                            context.WorkflowRunId, ruleCache, resource.ResourceId, sourceJson, preMappingHops,
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
                ["skippedResourceTypes"] = skippedResourceTypes.Count > 0 ? skippedResourceTypes.Distinct().ToArray() : null
            });
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
        Guid workflowRunId,
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
                    destinationType, resourceType, targetField, workflowRunId, sourceSystem,
                    ToRuleAuthoringSourceFieldFormat(resourceType, sourceField), cancellationToken);
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
    /// Resolves one resource type's fields + destination object, preferring (in order): <c>mappingProfileIds</c>
    /// — a JSON object of <c>{resourceType: mappingProfileId}</c> the build endpoint stamps onto this exact node
    /// (see WorkflowEndpoints.cs's Mappings step) — the id THIS node itself saved, so resolving by it can never
    /// pick up a different workflow's profile; the legacy single <c>mappingProfileId</c> (one resource per node,
    /// pre-dating multi-resource destinations) — only for the node's OWN configured resource type, since it can
    /// only ever refer to one specific profile; and finally the node's own inline "fields"/"destinationObject"
    /// config (no repository composed, or a hand-authored node) — again only for its own configured resource
    /// type. Deliberately does NOT fall back to searching MappingProfile by the natural key (resourceType,
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

    private static readonly System.Text.RegularExpressions.Regex ArrayIndexAnnotation = new(@"\[[^\]]*\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Converts a mapping field's internal JsonPath format (e.g. "$.birthDate", or "$.code.coding[*].code" for
    /// a repeating element, from MappingFieldDto.JsonPath) into the "ResourceType.field" format the portal's
    /// rule-authoring UI saves <c>TransformationRule.SourceField</c> as (see
    /// field-mapping-join-popover.component.ts's saveRule/loadRuleFor, both built from
    /// MappingRow.sources[].fhirPath) — <see cref="EfTransformationRuleRepository.GetFieldScopedAsync"/>'s match
    /// on SourceField is an exact string comparison, so both sides of it must agree on one convention. The UI's
    /// is the one actually persisted, so this side has to match it, not the other way around.
    ///
    /// Two normalizations, both confirmed against real saved rows: strip the leading "$." (the UI's fhirPath has
    /// none), and strip every "[...]" index/wildcard annotation (the UI's fhirPath never carries these either,
    /// e.g. "Condition.code.coding.code" — not "code.coding[*].code" — regardless of which repeating instance
    /// the field mapping itself resolves at runtime). Without the second normalization specifically, a
    /// Field-scope rule on ANY array-nested source field — codings, identifiers, telecoms, names, essentially
    /// most of FHIR — could never resolve, silently falling through to "no rule → pass the value through
    /// unchanged" for every record (reproduced: Condition.code.coding[*].code vs the saved
    /// "Condition.code.coding.code").
    /// </summary>
    private static string? ToRuleAuthoringSourceFieldFormat(string resourceType, string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath))
        {
            return null;
        }

        var bare = jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath.TrimStart('$', '.');
        bare = ArrayIndexAnnotation.Replace(bare, string.Empty);
        return $"{resourceType}.{bare}";
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
        Guid workflowRunId,
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
                        hop.BeforeValueJson,
                        hop.AfterValueJson,
                        hop.Success,
                        hop.ErrorMessage,
                        null,
                        DateTimeOffset.UtcNow));
                }
            }

            if (!ruleCache.TryGetValue(cacheKey, out var rules))
            {
                rules = await _ruleResolver.ResolveAsync(
                    destinationType.Value, resourceType, destinationField, workflowRunId, sourceSystem,
                    ToRuleAuthoringSourceFieldFormat(resourceType, sourceField), cancellationToken);
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
                    SerializeLineageValue(value),
                    SerializeLineageValue(value),
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
                    SerializeLineageValue(hopInput),
                    result.Success ? SerializeLineageValue(result.Value) : null,
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
                : currentValue;
        }

        return (transformed ?? row, fhirWriteBackPatches, lineageEntries);
    }

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
        IResourceNormalizationService? normalizationService = null)
        : base(nodeType, outputContract)
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
