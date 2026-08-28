using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public sealed class UsCoreValidationNodeExecutor : PassThroughNodeExecutor
{
    public UsCoreValidationNodeExecutor(FHIRBridge.Application.Abstractions.Normalization.IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.UsCoreValidation, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class ConsentNodeExecutor : PassThroughNodeExecutor
{
    private readonly IConsentService? _consentService;
    private readonly IGovernancePolicyService? _governancePolicyService;

    public ConsentNodeExecutor(
        IConsentService? consentService = null,
        IGovernancePolicyService? governancePolicyService = null)
        : base(WorkflowNodeTypes.Consent, WorkflowDataContract.MappedRecordBatch)
    {
        _consentService = consentService;
        _governancePolicyService = governancePolicyService;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        if (_consentService is null && _governancePolicyService is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        var resourceInputs = PassThroughNodeExecutor.ReadResourceEnvelopes(inputs);
        if (resourceInputs.Count > 0)
        {
            var permittedResources = new List<ResourceEnvelope>();
            foreach (var resource in resourceInputs)
            {
                if (await IsPermittedAsync(context, resource.ResourceType, resource.ResourceId, cancellationToken))
                {
                    permittedResources.Add(resource);
                }
            }

            return new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                new ResourceBatch(permittedResources),
                WorkflowDataContract.ResourceBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["count"] = permittedResources.Count
                });
        }

        var permitted = new List<MappedDestinationRecord>();
        foreach (var record in ReadMappedRecords(inputs))
        {
            if (await IsPermittedAsync(context, record.ResourceType, record.SourceResourceId, cancellationToken))
            {
                permitted.Add(record);
            }
        }

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new MappedRecordBatch(permitted),
            WorkflowDataContract.MappedRecordBatch,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["count"] = permitted.Count
            });
    }

    private async Task<bool> IsPermittedAsync(
        WorkflowExecutionContext context,
        string resourceType,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        var consentDecision = _consentService?.Evaluate(resourceType, resourceId);
        if (consentDecision is { IsPermitted: false })
        {
            return false;
        }

        if (_governancePolicyService is null)
        {
            return true;
        }

        var governanceDecision = await _governancePolicyService.EvaluateAsync(
            new ResourceGovernanceContext(
                context.WorkflowRunId,
                null,
                resourceType,
                resourceId,
                "WorkflowNodeExecute",
                null,
                context.CorrelationId),
            cancellationToken);

        return governanceDecision.IsAllowed;
    }
}

public sealed class DeIdentificationNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly IDeIdentificationService? _deIdentificationService;
    private readonly IDataSetDeIdentificationService? _dataSetDeIdentificationService;

    public DeIdentificationNodeExecutor(
        IDeIdentificationService? deIdentificationService = null,
        IDataSetDeIdentificationService? dataSetDeIdentificationService = null)
        : base(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch)
    {
        _deIdentificationService = deIdentificationService;
        _dataSetDeIdentificationService = dataSetDeIdentificationService;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var resourceInputs = PassThroughNodeExecutor.ReadResourceEnvelopes(inputs).ToArray();
        var records = PassThroughNodeExecutor.ReadMappedRecords(inputs).ToArray();

        // Opt-out flag: a graph can carry raw (non-de-identified) data through this node while still satisfying the
        // DeIdentifiedBatch output contract. Defaults to true so existing behaviour is unchanged. The route→graph
        // projection sets it false so a launch mirrors the route path (which de-identifies only when governance asks).
        var deIdentifyEnabled = ReadBoolConfiguration(node, "deIdentify") ?? true;
        // Which DeIdentificationProfile this node's rules come from — unlike the route/governance path, a
        // canvas node has no destination to resolve a profile from. There's no config UI for this yet, so an
        // unset value falls back to the seeded default profile (FHIRBridge.Domain.Entities.DeIdentificationProfile.
        // DefaultProfileId) rather than silently skipping redaction — this node always redacted unconditionally
        // before profiles existed, and PHI passing through unredacted by default would be a real regression.
        var resolvedProfileId = Guid.TryParse(ReadStringConfiguration(node, "profileId"), out var profileId) && profileId != Guid.Empty
            ? profileId
            : FHIRBridge.Domain.Entities.DeIdentificationProfile.DefaultProfileId;

        if (!deIdentifyEnabled || (_deIdentificationService is null && _dataSetDeIdentificationService is null))
        {
            // Pass through unchanged (preserving resource envelopes when present) rather than the base placeholder,
            // so downstream mapping receives the real payloads.
            var passThroughRecords = resourceInputs.Length > 0
                ? (IReadOnlyCollection<object>)resourceInputs
                : records;

            return new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                new DeIdentifiedBatch(passThroughRecords),
                WorkflowDataContract.DeIdentifiedBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["deIdentified"] = false,
                    ["count"] = passThroughRecords.Count
                });
        }

        if (resourceInputs.Length > 0)
        {
            var deIdentifiedResources = new List<ResourceEnvelope>();
            // Threaded onto this node's output metadata (see WorkflowNodeOutputMetadataKeys.PreMappingRedactions)
            // so the downstream Mapping node can merge each field's PreMapping redaction into the same Field
            // Lineage chain as its own PostMapping hops — SafeHarborDeIdentificationService.DeIdentifyAsync is
            // the only place that knows what was actually changed at this stage.
            var redactionsByResourceId = new Dictionary<string, IReadOnlyList<DeIdentificationFieldHop>>();
            foreach (var resource in resourceInputs)
            {
                string sourceJson;
                if (_deIdentificationService is null)
                {
                    sourceJson = Convert.ToString(resource.Payload) ?? "{}";
                }
                else
                {
                    var result = await _deIdentificationService.DeIdentifyAsync(
                        new DeIdentificationRequest(
                            resource.ResourceType,
                            resource.ResourceId,
                            Convert.ToString(resource.Payload) ?? "{}",
                            [],
                            resolvedProfileId),
                        cancellationToken);
                    sourceJson = result.Json;
                    if (result.Hops.Count > 0)
                    {
                        redactionsByResourceId[resource.ResourceId] = result.Hops;
                    }
                }

                deIdentifiedResources.Add(resource with { Payload = sourceJson });
            }

            return new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                new DeIdentifiedBatch(deIdentifiedResources),
                WorkflowDataContract.DeIdentifiedBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["count"] = deIdentifiedResources.Count,
                    [WorkflowNodeOutputMetadataKeys.PreMappingRedactions] = redactionsByResourceId,
                });
        }

        var deIdentified = new List<MappedDestinationRecord>();

        if (_dataSetDeIdentificationService is not null && records.Length > 0)
        {
            var byResourceType = records.GroupBy(record => record.ResourceType);
            foreach (var group in byResourceType)
            {
                var result = await _dataSetDeIdentificationService.DeIdentifyAsync(
                    new DataSetDeIdentificationRequest(
                        group.Key,
                        group.Select(record => record.SourceJson ?? "{}").ToArray()),
                    cancellationToken);

                var paired = group.Zip(result.ResourcesJson);
                foreach (var (record, sourceJson) in paired)
                {
                    deIdentified.Add(record with { SourceJson = sourceJson });
                }
            }
        }
        else if (_deIdentificationService is not null)
        {
            foreach (var record in records)
            {
                var result = await _deIdentificationService.DeIdentifyAsync(
                    new DeIdentificationRequest(
                        record.ResourceType,
                        record.SourceResourceId,
                        record.SourceJson ?? "{}",
                        [],
                        resolvedProfileId),
                    cancellationToken);

                deIdentified.Add(record with { SourceJson = result.Json });
            }
        }

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new DeIdentifiedBatch(deIdentified),
            WorkflowDataContract.DeIdentifiedBatch,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["count"] = deIdentified.Count
            });
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new DeIdentifiedBatch(PassThroughNodeExecutor.ReadMappedRecords(inputs).ToArray());
}

public sealed class AuditLineageNodeExecutor : WorkflowNodeExecutorBase
{
    public AuditLineageNodeExecutor()
        : base(WorkflowNodeTypes.AuditLineage, WorkflowDataContract.AuditResult)
    {
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new AuditResult(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
}
