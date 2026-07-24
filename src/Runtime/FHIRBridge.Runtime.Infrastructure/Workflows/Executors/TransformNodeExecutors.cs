using System.Text.Json;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
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
        IReadOnlyCollection<MappingFieldDto> fields = ReadConfiguration<IReadOnlyCollection<MappingFieldDto>>(node, "fields") ?? [];
        var resourceType = ReadStringConfiguration(node, "resourceType") ?? "Patient";
        var destinationObject = ReadStringConfiguration(node, "destinationObject") ?? resourceType;

        // Resolve a real MappingProfile (fields + resource type + destination object) preferring the SAME
        // natural key (resourceType, sourceConnectionId, destinationId) MappingImportService/ConfigurationService
        // already de-duplicate on — this is what actually exists for this node's source/destination combination
        // right now, rather than trusting whatever mappingProfileId happened to get stamped onto the node at
        // the last save (which can go stale: a later save can mint a different profile for the same
        // combination, or the stamp can simply be wrong — see the mapping-profile-id bugs this guards against).
        MappingProfile? profile = null;
        if (_configurationRepository is not null)
        {
            if (Guid.TryParse(ReadStringConfiguration(node, "sourceConnectionId"), out var sourceConnectionId) &&
                Guid.TryParse(ReadStringConfiguration(node, "destinationId"), out var destinationId))
            {
                profile = await _configurationRepository.FindMappingProfileAsync(
                    resourceType, sourceConnectionId, destinationId, cancellationToken);
            }

            // Fallback for a node saved before sourceConnectionId/destinationId were stamped onto it, or where
            // the natural-key lookup finds nothing (e.g. the profile's own resourceType was edited since).
            if (profile is null && Guid.TryParse(ReadStringConfiguration(node, "mappingProfileId"), out var mappingProfileId))
            {
                profile = await _configurationRepository.GetMappingProfileAsync(mappingProfileId, cancellationToken);
            }

            if (profile is not null)
            {
                fields = profile.Fields.Select(ConfigurationMapper.ToDto).ToArray();
                resourceType = profile.ResourceType;
                destinationObject = profile.DestinationObject;
            }
        }

        var records = new List<MappedDestinationRecord>();

        foreach (var resource in PassThroughNodeExecutor.ReadResourceEnvelopes(inputs))
        {
            var sourceJson = Convert.ToString(resource.Payload) ?? "{}";
            var mapped = _mappingEngine?.Map(sourceJson, fields);
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

            // Parent row (Scalar/FirstItem/RejectIfMultiple/RepeatParent fields land here). Skipped only when
            // there's truly nothing to write — no parent-level field AND no child table either. A node whose
            // fields are entirely SeparateDestination still needs a parent row emitted (even with empty Values)
            // because the writer captures the child rows' FK value off THAT row's own write (OUTPUT INSERTED) —
            // dropping it would silently lose the child data instead of just writing an extra near-empty row.
            if (mapped.Values.Count > 0 || childTables is not null)
            {
                var dataset = _mappingMaterializer?.Materialize(destinationObject, mapped);
                foreach (var parentRow in dataset?.ParentRows ?? [mapped.Values])
                {
                    records.Add(new MappedDestinationRecord(
                        context.WorkflowRunId, resource.ResourceType, destinationObject, resource.ResourceId,
                        parentRow, sourceJson, childTables));
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

            records.Add(new MappedChildTableRecord(
                childTable.Name, fkField.ForeignKeyColumn!, fkField.ParentKeyColumn ?? "Id", childTable.Rows));
        }

        return records.Count > 0 ? records : null;
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new MappedRecordBatch(inputs.Select(input => input.Payload!).Where(payload => payload is not null).ToArray());
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
