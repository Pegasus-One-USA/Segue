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
    private readonly IConfigurationRepository? _configurationRepository;

    public MappingNodeExecutor(
        IJsonMappingEngine? mappingEngine = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch)
    {
        _mappingEngine = mappingEngine;
        _configurationRepository = configurationRepository;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var records = new List<MappedDestinationRecord>();
        // One timestamp for the whole run so every row this node writes shares the same @now / WrittenOnUtc value.
        var runTimestampUtc = DateTime.UtcNow;

        // FHIR-repository passthrough (e.g. Aidbox): the destination wizard's Step 3 "Send FHIR resources as-is"
        // choice (the default) is stamped onto this synthetic Field Mapping node's own config as dest_fhirMapMode —
        // see WorkflowGraphMapperService.syntheticMappingRequest(), which spreads the destination node's fields
        // (including dest_fhirMapMode and destinationTransformId) onto this node verbatim. A FHIR-repository
        // destination is spec-owned (docs/backend/14-mapping-profile-master-screen-plan.md) — it has no target
        // columns to map into, so every resource is emitted unchanged rather than routed through the field-mapping
        // engine below, which requires a configured field list and would otherwise silently emit zero records for
        // any resource type nobody explicitly mapped. "customize" mode (rename/redact/translate a handful of
        // fields) is intentionally NOT handled here yet — its rule list has no backend translation built (still a
        // UI-only stub) — so it falls through to the normal field-mapping path below unchanged.
        var isFhirPassthrough =
            string.Equals(ReadStringConfiguration(node, "destinationTransformId"), "dest-fhir", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ReadStringConfiguration(node, "dest_fhirMapMode"), "customize", StringComparison.OrdinalIgnoreCase);

        if (isFhirPassthrough)
        {
            foreach (var resource in PassThroughNodeExecutor.ReadResourceEnvelopes(inputs))
            {
                var passthroughJson = Convert.ToString(resource.Payload) ?? "{}";
                records.Add(new MappedDestinationRecord(
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
                new MappedRecordBatch(records),
                WorkflowDataContract.MappedRecordBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["count"] = records.Count,
                    ["passthrough"] = true
                });
        }

        var resourceConfigs = await ResolveResourceMappingConfigsAsync(node, cancellationToken);

        foreach (var resource in PassThroughNodeExecutor.ReadResourceEnvelopes(inputs))
        {
            // A destination selecting multiple resources (e.g. Patient + Observation + Condition) has one config
            // entry per resource type here — each with its own fields and destination object. A resource type this
            // node has no configured mapping for is skipped entirely rather than mapped with another resource
            // type's fields (which previously produced rows with only the coincidentally-shared "id" populated and
            // every other column blank/wrong).
            if (!resourceConfigs.TryGetValue(resource.ResourceType, out var config))
            {
                continue;
            }

            var sourceJson = Convert.ToString(resource.Payload) ?? "{}";
            // Pipeline/runtime values a @token field can draw from (audit/lineage columns not present in the source
            // FHIR document): the run id, a shared write timestamp, and the resource's own type/id.
            var systemValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["@runId"] = context.WorkflowRunId,
                ["@now"] = runTimestampUtc,
                ["@resourceType"] = resource.ResourceType,
                ["@sourceResourceId"] = resource.ResourceId,
            };
            var mapped = _mappingEngine?.Map(sourceJson, config.Fields, systemValues);

            // Parent row (Scalar/FirstItem/RejectIfMultiple fields land here). Skipped when every field on this
            // node uses SeparateDestination, so a node dedicated to a child table doesn't emit an empty parent row.
            if (mapped is null || mapped.Values.Count > 0)
            {
                records.Add(new MappedDestinationRecord(
                    context.WorkflowRunId,
                    resource.ResourceType,
                    config.DestinationObject,
                    resource.ResourceId,
                    mapped?.Values ?? new Dictionary<string, object?>(),
                    sourceJson));
            }

            // ArrayPolicy.SeparateDestination rows: one record per array element, routed to its own child table.
            foreach (var childTable in mapped?.ChildTables ?? [])
            {
                foreach (var row in childTable.Rows)
                {
                    var values = row
                        .Where(kv => !string.Equals(kv.Key, "RowIndex", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                    records.Add(new MappedDestinationRecord(
                        context.WorkflowRunId,
                        resource.ResourceType,
                        childTable.Name,
                        resource.ResourceId,
                        values,
                        sourceJson));
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
                ["count"] = records.Count
            });
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new MappedRecordBatch(inputs.Select(input => input.Payload!).Where(payload => payload is not null).ToArray());

    private readonly record struct ResourceMappingConfig(string DestinationObject, IReadOnlyCollection<MappingFieldDto> Fields);

    /// <summary>
    /// Resolves this node's mapping configuration, keyed by FHIR resource type. Prefers <c>mappingProfileIds</c> —
    /// a JSON object of <c>{resourceType: mappingProfileId}</c> the build endpoint stamps when a destination
    /// selects more than one resource (see WorkflowEndpoints.cs's Mappings step) — resolving each referenced
    /// MappingProfile from the database. Falls back to the legacy single <c>mappingProfileId</c> (one resource per
    /// node, pre-dating multi-resource destinations), and finally to the node's own inline
    /// "resourceType"/"destinationObject"/"fields" config (no repository composed, or a hand-authored node) —
    /// unchanged behavior for every node that predates multi-resource support.
    /// </summary>
    private async Task<Dictionary<string, ResourceMappingConfig>> ResolveResourceMappingConfigsAsync(
        WorkflowNode node, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ResourceMappingConfig>(StringComparer.OrdinalIgnoreCase);

        if (_configurationRepository is not null)
        {
            foreach (var profileId in ReadProfileIds(node))
            {
                var profile = await _configurationRepository.GetMappingProfileAsync(profileId, cancellationToken);
                if (profile is null)
                {
                    continue;
                }

                result[profile.ResourceType] = new ResourceMappingConfig(
                    profile.DestinationObject,
                    profile.Fields.Select(ConfigurationMapper.ToDto).Where(f => f.IsEnabled).ToArray());
            }
        }

        if (result.Count > 0)
        {
            return result;
        }

        // Legacy/offline fallback: the node's own inline config describes exactly one resource type.
        var resourceType = ReadStringConfiguration(node, "resourceType") ?? "Patient";
        var destinationObject = ReadStringConfiguration(node, "destinationObject") ?? resourceType;
        var fields = ReadConfiguration<IReadOnlyCollection<MappingFieldDto>>(node, "fields") ?? [];
        result[resourceType] = new ResourceMappingConfig(destinationObject, fields);
        return result;
    }

    private static IReadOnlyList<Guid> ReadProfileIds(WorkflowNode node)
    {
        var mappingProfileIds = ReadConfiguration<Dictionary<string, string>>(node, "mappingProfileIds");
        if (mappingProfileIds is { Count: > 0 })
        {
            return mappingProfileIds.Values
                .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .ToArray();
        }

        var single = ReadStringConfiguration(node, "mappingProfileId");
        return Guid.TryParse(single, out var singleId) ? [singleId] : [];
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
