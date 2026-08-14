using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;

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

    public MappingNodeExecutor(
        IJsonMappingEngine? mappingEngine = null,
        IMappingMaterializer? mappingMaterializer = null,
        IConfigurationRepository? configurationRepository = null,
        IEffectiveRuleResolver? ruleResolver = null,
        ITransformNodeRegistry? transformNodeRegistry = null,
        ISystemSettingsCache? settingsCache = null,
        IAppSecretAccessor? secretAccessor = null)
        : base(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch)
    {
        _mappingEngine = mappingEngine;
        _mappingMaterializer = mappingMaterializer;
        _configurationRepository = configurationRepository;
        _ruleResolver = ruleResolver;
        _transformNodeRegistry = transformNodeRegistry;
        _settingsCache = settingsCache;
        _secretAccessor = secretAccessor;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var configuredFields = ReadConfiguration<IReadOnlyCollection<MappingFieldDto>>(node, "fields") ?? [];
        var configuredResourceType = ReadStringConfiguration(node, "resourceType") ?? "Patient";
        var configuredDestinationObject = ReadStringConfiguration(node, "destinationObject") ?? configuredResourceType;
        Guid.TryParse(ReadStringConfiguration(node, "sourceConnectionId"), out var sourceConnectionId);
        Guid.TryParse(ReadStringConfiguration(node, "destinationId"), out var destinationId);

        // Resolved once per node execution (not per record) — both are stable for this whole batch, and the
        // transform-rule resolver only needs them, never the full entities.
        var destinationType = await ResolveDestinationTypeAsync(destinationId, cancellationToken);
        var sourceSystem = await ResolveSourceSystemAsync(sourceConnectionId, cancellationToken);

        // Whole-resource FHIR destinations (Medplum, FHIR repository) persist the source resource itself
        // (MappedDestinationRecord.SourceJson), not a set of mapped relational columns — so they legitimately have
        // NO field mappings, and the "needs at least one mapped Value" gates below (which exist to avoid writing
        // bogus empty rows into a relational table) would otherwise drop every resource, silently landing zero
        // records. For these destinations we always emit one carrier record per resource, carrying SourceJson.
        var wholeResourceFhir = destinationType is DestinationType.Medplum or DestinationType.FhirRepository;
        // Caches each field's resolved rule chain for the lifetime of this ExecuteAsync call — the same
        // (resourceType, destinationField, sourceField) combination recurs once per record in the batch, and
        // re-querying the resolver/repository for every single record would be wasted round trips for a rule
        // set that can't have changed mid-batch.
        var ruleCache = new Dictionary<string, IReadOnlyList<TransformationRule>>();

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
                sourceConnectionId, destinationId, cancellationToken);

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
                skippedResourceTypes.Add(resourceType);
                continue;
            }

            // Which source JsonPath fed each destination column — lets the transform-rule resolver prefer a
            // source-field-specific rule (see EffectiveRuleResolver.PreferSourceFieldSpecific) the same way it
            // already prefers a source-system-specific one, without the resolver needing to re-derive it itself.
            var sourceFieldByTarget = fields
                .GroupBy(f => f.TargetField, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().JsonPath, StringComparer.OrdinalIgnoreCase);

            foreach (var resource in group)
            {
                var sourceJson = Convert.ToString(resource.Payload) ?? "{}";
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
                        var transformedRow = await ApplyTransformRulesAsync(
                            parentRow, resourceType, sourceFieldByTarget, destinationType, sourceSystem,
                            context.WorkflowRunId, ruleCache, resource.ResourceId, cancellationToken);

                        records.Add(new MappedDestinationRecord(
                            context.WorkflowRunId, resource.ResourceType, destinationObject, resource.ResourceId,
                            transformedRow, sourceJson, childTables, referenceLookups));
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
    /// Resolves one resource type's fields + destination object, preferring (in order): the real MappingProfile
    /// found by the natural key (resourceType, sourceConnectionId, destinationId) MappingImportService/
    /// ConfigurationService already de-duplicate on — what actually exists for this node's source/destination
    /// combination right now, rather than trusting a possibly-stale stamped id; <c>mappingProfileIds</c> — a JSON
    /// object of <c>{resourceType: mappingProfileId}</c> the build endpoint stamps when a destination selects more
    /// than one resource (see WorkflowEndpoints.cs's Mappings step), for a node saved before sourceConnectionId/
    /// destinationId were stamped onto it; the legacy single <c>mappingProfileId</c> (one resource per node,
    /// pre-dating multi-resource destinations) — only for the node's OWN configured resource type, since it can
    /// only ever refer to one specific profile; and finally the node's own inline "fields"/"destinationObject"
    /// config (no repository composed, or a hand-authored node) — again only for its own configured resource
    /// type. Returns null Fields when nothing resolves — the caller skips that resource type entirely rather than
    /// guessing with another resource type's shape.
    /// </summary>
    private async Task<(IReadOnlyCollection<MappingFieldDto>? Fields, string DestinationObject)> ResolveResourceMappingAsync(
        WorkflowNode node,
        string resourceType,
        string configuredResourceType,
        IReadOnlyCollection<MappingFieldDto> configuredFields,
        string configuredDestinationObject,
        Guid sourceConnectionId,
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        if (_configurationRepository is not null)
        {
            if (sourceConnectionId != Guid.Empty && destinationId != Guid.Empty)
            {
                var exactMatch = await _configurationRepository.FindMappingProfileAsync(
                    resourceType, sourceConnectionId, destinationId, cancellationToken);
                if (exactMatch is not null)
                {
                    return (exactMatch.Fields.Select(ConfigurationMapper.ToDto).Where(f => f.IsEnabled).ToArray(), exactMatch.DestinationObject);
                }
            }

            if (ReadProfileIds(node).TryGetValue(resourceType, out var profileId))
            {
                var profile = await _configurationRepository.GetMappingProfileAsync(profileId, cancellationToken);
                if (profile is not null)
                {
                    return (profile.Fields.Select(ConfigurationMapper.ToDto).Where(f => f.IsEnabled).ToArray(), profile.DestinationObject);
                }
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

    private async Task<DestinationType?> ResolveDestinationTypeAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        if (_configurationRepository is null || destinationId == Guid.Empty)
        {
            return null;
        }

        var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
        return destination?.DestinationType;
    }

    private async Task<string?> ResolveSourceSystemAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        if (_configurationRepository is null || sourceConnectionId == Guid.Empty)
        {
            return null;
        }

        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        return sourceConnection?.SourceSystemType.ToString();
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
    private async Task<IReadOnlyDictionary<string, object?>> ApplyTransformRulesAsync(
        IReadOnlyDictionary<string, object?> row,
        string resourceType,
        IReadOnlyDictionary<string, string> sourceFieldByTarget,
        DestinationType? destinationType,
        string? sourceSystem,
        Guid workflowRunId,
        Dictionary<string, IReadOnlyList<TransformationRule>> ruleCache,
        string resourceId,
        CancellationToken cancellationToken)
    {
        if (_ruleResolver is null || _transformNodeRegistry is null || destinationType is null || row.Count == 0)
        {
            return row;
        }

        var hidden = _settingsCache is null
            || await _settingsCache.GetBoolAsync(
                TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, cancellationToken);
        if (hidden)
        {
            return row;
        }

        Dictionary<string, object?>? transformed = null;

        foreach (var (destinationField, value) in row)
        {
            sourceFieldByTarget.TryGetValue(destinationField, out var sourceField);
            var cacheKey = $"{resourceType}|{destinationField}|{sourceField}";

            if (!ruleCache.TryGetValue(cacheKey, out var rules))
            {
                rules = await _ruleResolver.ResolveAsync(
                    destinationType.Value, resourceType, destinationField, workflowRunId, sourceSystem, sourceField, cancellationToken);
                ruleCache[cacheKey] = rules;
            }

            if (rules.Count == 0)
            {
                continue;
            }

            var currentValue = value;
            foreach (var rule in rules)
            {
                if (TransformNullPolicy.IsNullOrEmpty(currentValue))
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

                // Reserved, caller-populated key (never persisted) — DateMathAge's per-patient seeded shift
                // needs "the patient this row belongs to," which for a Patient-resource row is simply its own
                // id; other resource types (Observation, Encounter, ...) would need reference resolution this
                // generic loop doesn't have, so they fall back to DateMathAge's fixed `days` config instead.
                if (rule.NodeType == TransformNodeType.DateMathAge && string.Equals(resourceType, "Patient", StringComparison.OrdinalIgnoreCase))
                {
                    config["_patientId"] = resourceId;
                }

                var result = TransformNodeApplier.ExecuteWithArrayMode(node, currentValue, config, secret, rule.ArrayMode);
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

        return transformed ?? row;
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

    private static Dictionary<string, Guid> ReadProfileIds(WorkflowNode node)
    {
        var mappingProfileIds = ReadConfiguration<Dictionary<string, string>>(node, "mappingProfileIds");
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (mappingProfileIds is { Count: > 0 })
        {
            foreach (var (resourceType, id) in mappingProfileIds)
            {
                if (Guid.TryParse(id, out var parsed))
                {
                    result[resourceType] = parsed;
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        var single = ReadStringConfiguration(node, "mappingProfileId");
        var legacyResourceType = ReadStringConfiguration(node, "resourceType") ?? "Patient";
        if (Guid.TryParse(single, out var singleId))
        {
            result[legacyResourceType] = singleId;
        }

        return result;
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
                NormalizedResourceBatch batch => batch.Resources.Select((resource, index) => new ResourceEnvelope("Patient", index.ToString(), resource)),
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
