using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public sealed class EpicSourceNodeExecutor : SourceNodeExecutor
{
    public EpicSourceNodeExecutor(IFhirSourceClientFactory? sourceClientFactory = null)
        : base(WorkflowNodeTypes.EpicSource, RuntimeSourceType.Epic, sourceClientFactory)
    {
    }
}

public sealed class CernerSourceNodeExecutor : SourceNodeExecutor
{
    public CernerSourceNodeExecutor(IFhirSourceClientFactory? sourceClientFactory = null)
        : base(WorkflowNodeTypes.CernerSource, RuntimeSourceType.Cerner, sourceClientFactory)
    {
    }
}

public sealed class EClinicalWorksSourceNodeExecutor : SourceNodeExecutor
{
    public EClinicalWorksSourceNodeExecutor(IFhirSourceClientFactory? sourceClientFactory = null)
        : base(WorkflowNodeTypes.EClinicalWorksSource, RuntimeSourceType.Healow, sourceClientFactory)
    {
    }
}

public sealed class AthenahealthSourceNodeExecutor : SourceNodeExecutor
{
    public AthenahealthSourceNodeExecutor(IFhirSourceClientFactory? sourceClientFactory = null)
        : base(WorkflowNodeTypes.AthenahealthSource, RuntimeSourceType.GenericFhir, sourceClientFactory)
    {
    }
}

public sealed class AllscriptsSourceNodeExecutor : SourceNodeExecutor
{
    public AllscriptsSourceNodeExecutor(IFhirSourceClientFactory? sourceClientFactory = null)
        : base(WorkflowNodeTypes.AllscriptsSource, RuntimeSourceType.Allscripts, sourceClientFactory)
    {
    }
}

public sealed class MeditechSourceNodeExecutor : SourceNodeExecutor
{
    public MeditechSourceNodeExecutor(IFhirSourceClientFactory? sourceClientFactory = null)
        : base(WorkflowNodeTypes.MeditechSource, RuntimeSourceType.MeditechGreenfield, sourceClientFactory)
    {
    }
}

public sealed class GenericFhirSourceNodeExecutor : SourceNodeExecutor
{
    public GenericFhirSourceNodeExecutor(IFhirSourceClientFactory? sourceClientFactory = null)
        : base(WorkflowNodeTypes.GenericFhirSource, RuntimeSourceType.GenericFhir, sourceClientFactory)
    {
    }
}

public sealed class SampleSourceNodeExecutor : SourceNodeExecutor
{
    public SampleSourceNodeExecutor(IFhirSourceClientFactory? sourceClientFactory = null)
        : base(WorkflowNodeTypes.SampleSource, RuntimeSourceType.Sample, sourceClientFactory)
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

    protected SourceNodeExecutor(
        string nodeType,
        RuntimeSourceType sourceType,
        IFhirSourceClientFactory? sourceClientFactory)
        : base(nodeType, WorkflowDataContract.ResourceBatch)
    {
        _sourceType = sourceType;
        _sourceClientFactory = sourceClientFactory;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var source = ReadConfiguration<FhirSourceConfiguration>(node, "source")
            ?? ReadConfiguration<FhirSourceConfiguration>(node);
        var resourceType = ReadStringConfiguration(node, "resourceType") ?? "Patient";

        if (_sourceClientFactory is null || source is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        if (source.SourceType != _sourceType)
        {
            source = source with { SourceType = _sourceType };
        }

        var client = _sourceClientFactory.Create(_sourceType);
        var resources = await client.SearchAsync(resourceType, source, cancellationToken);
        var payload = new ResourceBatch(resources.Select(resource => new ResourceEnvelope(
            resource.ResourceType,
            resource.ResourceId ?? string.Empty,
            resource.RawJson)).ToArray());

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            payload,
            WorkflowDataContract.ResourceBatch,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["resourceType"] = resourceType,
                ["count"] = resources.Count
            });
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new ResourceBatch([]);
}
