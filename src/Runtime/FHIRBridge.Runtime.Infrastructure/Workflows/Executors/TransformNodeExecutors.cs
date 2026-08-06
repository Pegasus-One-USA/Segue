using System.Text.Json;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
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

    public MappingNodeExecutor(
        IJsonMappingEngine? mappingEngine = null,
        IMappingMaterializer? mappingMaterializer = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch)
    {
        _mappingEngine = mappingEngine;
        _mappingMaterializer = mappingMaterializer;
        _configurationRepository = configurationRepository;
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
                // No profile exists for this resource type — nothing tells us how to map it, so skip it rather
                // than guess; guessing (reusing a different resource type's fields) is exactly the
                // silent-corruption bug this method guards against.
                skippedResourceTypes.Add(resourceType);
                continue;
            }

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
                if (mapped.Values.Count > 0 || childTables is not null)
                {
                    var dataset = _mappingMaterializer?.Materialize(destinationObject, mapped);
                    foreach (var parentRow in dataset?.ParentRows ?? [mapped.Values])
                    {
                        records.Add(new MappedDestinationRecord(
                            context.WorkflowRunId, resource.ResourceType, destinationObject, resource.ResourceId,
                            parentRow, sourceJson, childTables, referenceLookups));
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
    /// found by the natural key (resourceType, sourceConnectionId, destinationId, this node's own
    /// WorkflowDefinitionId) MappingImportService/ConfigurationService already de-duplicate on — what actually
    /// exists for THIS workflow's own source/destination combination right now, rather than trusting a possibly-
    /// stale stamped id or picking up a different workflow's profile for the same triple (see
    /// MappingProfile.WorkflowId); <c>mappingProfileIds</c> — a JSON
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
                    resourceType, sourceConnectionId, destinationId, node.WorkflowDefinitionId, cancellationToken);
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
