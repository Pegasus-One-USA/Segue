using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

public sealed class RankedWorkflowOrchestratorTests
{
    [Fact]
    public async Task ExecuteAsync_executes_nodes_in_rank_and_subrank_order()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "ranked", 1);
        var source = AddNode(workflow, WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 0);
        var consent = AddNode(workflow, WorkflowNodeTypes.Consent, WorkflowNodeCategory.Compliance, 10);
        var usCore = AddNode(workflow, WorkflowNodeTypes.UsCoreValidation, WorkflowNodeCategory.Compliance, 20);
        var normalization = AddNode(workflow, WorkflowNodeTypes.Normalization, WorkflowNodeCategory.Transform, 30);
        var terminology = AddNode(workflow, WorkflowNodeTypes.Terminology, WorkflowNodeCategory.Transform, 40);
        var deIdentification = AddNode(workflow, WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50);
        var mapping = AddNode(workflow, WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60);
        var destination = AddNode(workflow, WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        var audit = AddNode(workflow, WorkflowNodeTypes.AuditLineage, WorkflowNodeCategory.Compliance, 80);
        workflow.AddEdge(source.Id, consent.Id);
        workflow.AddEdge(consent.Id, usCore.Id);
        workflow.AddEdge(usCore.Id, normalization.Id);
        workflow.AddEdge(normalization.Id, terminology.Id);
        workflow.AddEdge(terminology.Id, deIdentification.Id);
        workflow.AddEdge(deIdentification.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, destination.Id);
        workflow.AddEdge(destination.Id, audit.Id);

        var calls = new List<string>();
        var orchestrator = CreateOrchestrator(
            new RecordingExecutor(WorkflowNodeTypes.EpicSource, calls, WorkflowDataContract.ResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.Consent, calls, WorkflowDataContract.ResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.UsCoreValidation, calls, WorkflowDataContract.NormalizedResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.Normalization, calls, WorkflowDataContract.NormalizedResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.Terminology, calls, WorkflowDataContract.NormalizedResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.DeIdentification, calls, WorkflowDataContract.DeIdentifiedBatch),
            new RecordingExecutor(WorkflowNodeTypes.Mapping, calls, WorkflowDataContract.MappedRecordBatch),
            new RecordingExecutor(WorkflowNodeTypes.SqlServerDestination, calls, WorkflowDataContract.DestinationWriteResult),
            new RecordingExecutor(WorkflowNodeTypes.AuditLineage, calls, WorkflowDataContract.AuditResult));

        var result = await orchestrator.ExecuteAsync(workflow, CreateContext());

        calls.Should().Equal(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeTypes.Consent,
            WorkflowNodeTypes.UsCoreValidation,
            WorkflowNodeTypes.Normalization,
            WorkflowNodeTypes.Terminology,
            WorkflowNodeTypes.DeIdentification,
            WorkflowNodeTypes.Mapping,
            WorkflowNodeTypes.SqlServerDestination,
            WorkflowNodeTypes.AuditLineage);
        result.WorkflowRun.Status.Should().Be(WorkflowRunStatus.Succeeded);
        result.WorkflowRun.NodeRuns.Should().HaveCount(9);
    }

    [Fact]
    public async Task ExecuteAsync_passes_fan_out_and_fan_in_outputs_to_downstream_nodes()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "fan-in-out", 1);
        var epic = AddNode(workflow, WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 0);
        var cerner = AddNode(workflow, WorkflowNodeTypes.CernerSource, WorkflowNodeCategory.Source, 0);
        var consent = AddNode(workflow, WorkflowNodeTypes.Consent, WorkflowNodeCategory.Compliance, 10);
        var usCore = AddNode(workflow, WorkflowNodeTypes.UsCoreValidation, WorkflowNodeCategory.Compliance, 20);
        var normalization = AddNode(workflow, WorkflowNodeTypes.Normalization, WorkflowNodeCategory.Transform, 30);
        var terminology = AddNode(workflow, WorkflowNodeTypes.Terminology, WorkflowNodeCategory.Transform, 40);
        var deIdentification = AddNode(workflow, WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50);
        var mapping = AddNode(workflow, WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60);
        var sql = AddNode(workflow, WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        var blob = AddNode(workflow, WorkflowNodeTypes.BlobDestination, WorkflowNodeCategory.Destination, 70);
        workflow.AddEdge(epic.Id, consent.Id);
        workflow.AddEdge(cerner.Id, consent.Id);
        workflow.AddEdge(consent.Id, usCore.Id);
        workflow.AddEdge(usCore.Id, normalization.Id);
        workflow.AddEdge(normalization.Id, terminology.Id);
        workflow.AddEdge(terminology.Id, deIdentification.Id);
        workflow.AddEdge(deIdentification.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, sql.Id);
        workflow.AddEdge(mapping.Id, blob.Id);

        var destinationInputs = Array.Empty<WorkflowNodeOutput>();
        var blobInputs = Array.Empty<WorkflowNodeOutput>();
        var orchestrator = CreateOrchestrator(
            new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "epic"),
            new PayloadExecutor(WorkflowNodeTypes.CernerSource, WorkflowDataContract.ResourceBatch, _ => "cerner"),
            new PayloadExecutor(WorkflowNodeTypes.Consent, WorkflowDataContract.ResourceBatch, inputs => $"consent:{string.Join("+", inputs.Select(input => input.Payload))}"),
            new PayloadExecutor(WorkflowNodeTypes.UsCoreValidation, WorkflowDataContract.NormalizedResourceBatch, inputs => $"uscore:{inputs.Single().Payload}"),
            new PayloadExecutor(WorkflowNodeTypes.Normalization, WorkflowDataContract.NormalizedResourceBatch, inputs => $"norm:{inputs.Single().Payload}"),
            new PayloadExecutor(WorkflowNodeTypes.Terminology, WorkflowDataContract.NormalizedResourceBatch, inputs => $"term:{inputs.Single().Payload}"),
            new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => $"deid:{inputs.Single().Payload}"),
            new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => $"mapping:{inputs.Single().Payload}"),
            new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs =>
            {
                destinationInputs = inputs.ToArray();
                return string.Join(",", inputs.Select(input => input.Payload));
            }),
            new PayloadExecutor(WorkflowNodeTypes.BlobDestination, WorkflowDataContract.DestinationWriteResult, inputs =>
            {
                blobInputs = inputs.ToArray();
                return string.Join(",", inputs.Select(input => input.Payload));
            }));

        var result = await orchestrator.ExecuteAsync(workflow, CreateContext());

        destinationInputs.Select(input => input.Payload).Should().Equal("mapping:deid:term:norm:uscore:consent:epic+cerner");
        blobInputs.Select(input => input.Payload).Should().Equal("mapping:deid:term:norm:uscore:consent:epic+cerner");
        result.OutputsByNodeId[sql.Id].Payload.Should().Be("mapping:deid:term:norm:uscore:consent:epic+cerner");
    }

    [Fact]
    public async Task ExecuteAsync_records_lineage_for_each_node_run()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "lineage", 1);
        var source = AddNode(workflow, WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 0);
        var deIdentification = AddNode(workflow, WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50);
        var mapping = AddNode(workflow, WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60);
        var destination = AddNode(workflow, WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        workflow.AddEdge(source.Id, deIdentification.Id);
        workflow.AddEdge(deIdentification.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, destination.Id);
        var orchestrator = CreateOrchestrator(
            new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "bundle"),
            new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => inputs.Single().Payload),
            new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
            new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs => inputs.Single().Payload));

        var result = await orchestrator.ExecuteAsync(workflow, CreateContext());

        result.WorkflowRun.NodeRuns.Should().OnlyContain(nodeRun =>
            nodeRun.Status == WorkflowRunStatus.Succeeded
            && !string.IsNullOrWhiteSpace(nodeRun.LineageJson));
    }

    private static RankedWorkflowOrchestrator CreateOrchestrator(params IWorkflowNodeExecutor[] executors)
        => new(new WorkflowGraphValidator(), new WorkflowNodeExecutorRegistry(executors));

    private static WorkflowExecutionContext CreateContext()
        => new(Guid.NewGuid(), Guid.NewGuid(), "test-correlation");

    private static WorkflowNode AddNode(
        WorkflowDefinition workflow,
        string nodeType,
        WorkflowNodeCategory category,
        int rank,
        string configurationJson = "{}")
        => workflow.AddNode(nodeType, category, rank, configurationJson: configurationJson);

    private sealed class RecordingExecutor : IWorkflowNodeExecutor
    {
        private readonly List<string> _calls;

        private readonly WorkflowDataContract _contract;

        public RecordingExecutor(string nodeType, List<string> calls, WorkflowDataContract contract)
        {
            NodeType = nodeType;
            _calls = calls;
            _contract = contract;
        }

        public string NodeType { get; }

        public Task<WorkflowNodeOutput> ExecuteAsync(
            WorkflowExecutionContext context,
            WorkflowNode node,
            IReadOnlyCollection<WorkflowNodeOutput> inputs,
            CancellationToken cancellationToken)
        {
            _calls.Add(NodeType);
            return Task.FromResult(new WorkflowNodeOutput(node.Id, node.NodeType, NodeType, _contract));
        }
    }

    private sealed class PayloadExecutor : IWorkflowNodeExecutor
    {
        private readonly Func<IReadOnlyCollection<WorkflowNodeOutput>, object?> _execute;
        private readonly WorkflowDataContract _contract;

        public PayloadExecutor(
            string nodeType,
            WorkflowDataContract contract,
            Func<IReadOnlyCollection<WorkflowNodeOutput>, object?> execute)
        {
            NodeType = nodeType;
            _contract = contract;
            _execute = execute;
        }

        public string NodeType { get; }

        public Task<WorkflowNodeOutput> ExecuteAsync(
            WorkflowExecutionContext context,
            WorkflowNode node,
            IReadOnlyCollection<WorkflowNodeOutput> inputs,
            CancellationToken cancellationToken)
            => Task.FromResult(new WorkflowNodeOutput(node.Id, node.NodeType, _execute(inputs), _contract));
    }
}
