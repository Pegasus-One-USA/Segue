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
        var consentDecision = _consentService?.Evaluate(context.TenantId, resourceType, resourceId);
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
                context.TenantId,
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
        if (_deIdentificationService is null && _dataSetDeIdentificationService is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        if (resourceInputs.Length > 0)
        {
            var deIdentifiedResources = new List<ResourceEnvelope>();
            foreach (var resource in resourceInputs)
            {
                var sourceJson = _deIdentificationService is null
                    ? Convert.ToString(resource.Payload) ?? "{}"
                    : await _deIdentificationService.DeIdentifyAsync(
                        new DeIdentificationRequest(
                            context.TenantId,
                            resource.ResourceType,
                            resource.ResourceId,
                            Convert.ToString(resource.Payload) ?? "{}",
                            []),
                        cancellationToken);

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
                    ["count"] = deIdentifiedResources.Count
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
                        context.TenantId,
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
                var sourceJson = await _deIdentificationService.DeIdentifyAsync(
                    new DeIdentificationRequest(
                        context.TenantId,
                        record.ResourceType,
                        record.SourceResourceId,
                        record.SourceJson ?? "{}",
                        []),
                    cancellationToken);

                deIdentified.Add(record with { SourceJson = sourceJson });
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
