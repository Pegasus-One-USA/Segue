using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.Options;

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
    private readonly IFieldLineageStore? _fieldLineageStore;
    private readonly bool _fieldLineageEnabled;

    public MappingNodeExecutor(
        IJsonMappingEngine? mappingEngine = null,
        IConfigurationRepository? configurationRepository = null,
        IFieldLineageStore? fieldLineageStore = null,
        IOptions<FieldLineageOptions>? fieldLineageOptions = null)
        : base(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch)
    {
        _mappingEngine = mappingEngine;
        _configurationRepository = configurationRepository;
        _fieldLineageStore = fieldLineageStore;
        _fieldLineageEnabled = fieldLineageOptions?.Value.Enabled ?? false;
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

        // Option A: prefer a real MappingProfile referenced by id (fields + resource type + destination object).
        var mappingProfileId = ReadStringConfiguration(node, "mappingProfileId");
        Guid? resolvedMappingProfileId = Guid.TryParse(mappingProfileId, out var parsedMappingProfileId) ? parsedMappingProfileId : null;
        if (_configurationRepository is not null && resolvedMappingProfileId is { } profileId)
        {
            var profile = await _configurationRepository.GetMappingProfileAsync(profileId, cancellationToken);
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

            // Parent row (Scalar/FirstItem/RejectIfMultiple fields land here). Skipped when every field on this
            // node uses SeparateDestination, so a node dedicated to a child table doesn't emit an empty parent row.
            if (mapped is null || mapped.Values.Count > 0)
            {
                records.Add(new MappedDestinationRecord(
                    context.WorkflowRunId,
                    resource.ResourceType,
                    destinationObject,
                    resource.ResourceId,
                    mapped?.Values ?? new Dictionary<string, object?>(),
                    sourceJson));

                if (_fieldLineageEnabled && _fieldLineageStore is not null && mapped is not null)
                {
                    await RecordFieldLineageAsync(
                        context, resolvedMappingProfileId, resource.ResourceType, resource.ResourceId,
                        destinationObject, fields, mapped.Values, cancellationToken);
                }
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

    // One record per field rule that actually produced a parent-row column for this resource — answers "why does
    // this destination column hold this value" by capturing the source FHIR path and transformation, never the
    // resolved value itself (PHI-free, like the resource-level lineage trail). Child-table (SeparateDestination
    // array) fields are out of scope for now — mapping a child row's column back to its originating array field
    // definition isn't exposed by MappedRecordBatch's current shape.
    private async Task RecordFieldLineageAsync(
        WorkflowExecutionContext context,
        Guid? mappingProfileId,
        string resourceType,
        string? sourceResourceId,
        string destinationObject,
        IReadOnlyCollection<MappingFieldDto> fields,
        IReadOnlyDictionary<string, object?> mappedValues,
        CancellationToken cancellationToken)
    {
        foreach (var field in fields)
        {
            if (!mappedValues.ContainsKey(field.TargetField))
            {
                continue;
            }

            await _fieldLineageStore!.AppendAsync(
                new FieldLineageRecord(
                    context.WorkflowRunId,
                    mappingProfileId,
                    resourceType,
                    sourceResourceId,
                    field.JsonPath,
                    DescribeTransformation(field),
                    destinationObject,
                    field.TargetField,
                    DateTime.UtcNow),
                cancellationToken);
        }
    }

    private static string DescribeTransformation(MappingFieldDto field)
    {
        if (!string.IsNullOrWhiteSpace(field.TerminologySystemJsonPath) || !string.IsNullOrWhiteSpace(field.TerminologyCodeJsonPath))
        {
            return "TerminologyTranslation";
        }

        return string.IsNullOrWhiteSpace(field.NormalizationType) ? "DirectCopy" : field.NormalizationType;
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
