using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers the Phase 2 wiring: a workflow-graph run must produce the same PHI-free ResourceLineageRecord trail the
// older Configured Pipeline path already produces, without every node executor needing to know about ILineageTracker.
public sealed class LineageTrackingWorkflowNodeExecutorTests
{
    [Fact]
    public async Task Source_node_records_ResourceAccessed_lineage_from_its_own_output()
    {
        var (tracker, recorded) = CreateTracker();
        var inner = new FakeExecutor(WorkflowNodeTypes.EpicSource, (_, node, _) => new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new ResourceBatch([new ResourceEnvelope("Patient", "p1", "{}"), new ResourceEnvelope("Patient", "p2", "{}")]),
            WorkflowDataContract.ResourceBatch));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object);
        var node = BuildNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        await decorator.ExecuteAsync(context, node, [], CancellationToken.None);

        recorded.Should().HaveCount(2);
        recorded.Should().OnlyContain(r => r.Action == "ResourceAccessed" && r.PipelineRunId == context.WorkflowRunId);
        recorded.Select(r => r.SourceResourceId).Should().BeEquivalentTo("p1", "p2");
    }

    [Fact]
    public async Task Compliance_node_records_ResourceDeIdentified_lineage()
    {
        var (tracker, recorded) = CreateTracker();
        var inner = new FakeExecutor(WorkflowNodeTypes.DeIdentification, (_, node, _) => new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new DeIdentifiedBatch([new ResourceEnvelope("Patient", "p1", "SCRUBBED")]),
            WorkflowDataContract.DeIdentifiedBatch));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object);
        var node = BuildNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance);

        await decorator.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        recorded.Should().ContainSingle(r => r.Action == "ResourceDeIdentified" && r.SourceResourceId == "p1");
    }

    [Fact]
    public async Task Destination_node_records_ResourceWritten_lineage_from_its_inputs_not_its_output()
    {
        var (tracker, recorded) = CreateTracker();
        var mappedRecords = new MappedRecordBatch(
            [new MappedDestinationRecord(Guid.NewGuid(), "Patient", "dbo.Patient", "p1", new Dictionary<string, object?>())]);
        var inputs = new[]
        {
            new WorkflowNodeOutput(Guid.NewGuid(), WorkflowNodeTypes.Mapping, mappedRecords, WorkflowDataContract.MappedRecordBatch)
        };
        var inner = new FakeExecutor(WorkflowNodeTypes.SqlServerDestination, (_, node, _) => new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new DestinationWriteResult("dest-1", 1, DateTimeOffset.UtcNow),
            WorkflowDataContract.DestinationWriteResult));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object);
        var node = BuildNode(WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination);

        await decorator.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, inputs, CancellationToken.None);

        recorded.Should().ContainSingle(r => r.Action == "ResourceWritten" && r.SourceResourceId == "p1");
    }

    [Fact]
    public async Task Analytics_node_records_no_lineage()
    {
        var (tracker, recorded) = CreateTracker();
        var inner = new FakeExecutor(WorkflowNodeTypes.HedisMeasureReport, (_, node, _) => new WorkflowNodeOutput(
            node.Id, node.NodeType, new ResourceBatch([new ResourceEnvelope("Patient", "p1", "{}")]), WorkflowDataContract.ResourceBatch));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object);
        var node = BuildNode(WorkflowNodeTypes.HedisMeasureReport, WorkflowNodeCategory.Analytics);

        await decorator.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Registry_wraps_every_resolved_executor()
    {
        var (tracker, recorded) = CreateTracker();
        var inner = new FakeExecutor(WorkflowNodeTypes.SampleSource, (_, node, _) => new WorkflowNodeOutput(
            node.Id, node.NodeType, new ResourceBatch([new ResourceEnvelope("Patient", "p1", "{}")]), WorkflowDataContract.ResourceBatch));
        var registry = new LineageTrackingWorkflowNodeExecutorRegistry([inner], tracker.Object);
        var node = BuildNode(WorkflowNodeTypes.SampleSource, WorkflowNodeCategory.Source);

        var executor = registry.GetRequired(WorkflowNodeTypes.SampleSource);
        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        recorded.Should().ContainSingle(r => r.Action == "ResourceAccessed");
    }

    private static (Mock<ILineageTracker> Tracker, List<ResourceLineageRecord> Recorded) CreateTracker()
    {
        var recorded = new List<ResourceLineageRecord>();
        var tracker = new Mock<ILineageTracker>();
        tracker.Setup(t => t.RecordAsync(It.IsAny<ResourceLineageRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ResourceLineageRecord, CancellationToken>((record, _) => recorded.Add(record))
            .Returns(Task.CompletedTask);

        return (tracker, recorded);
    }

    private static WorkflowNode BuildNode(string nodeType, WorkflowNodeCategory category)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "lineage-test", 1);
        return workflow.AddNode(nodeType, category, rank: 0);
    }

    private sealed class FakeExecutor : IWorkflowNodeExecutor
    {
        private readonly Func<WorkflowExecutionContext, WorkflowNode, IReadOnlyCollection<WorkflowNodeOutput>, WorkflowNodeOutput> _execute;

        public FakeExecutor(
            string nodeType,
            Func<WorkflowExecutionContext, WorkflowNode, IReadOnlyCollection<WorkflowNodeOutput>, WorkflowNodeOutput> execute)
        {
            NodeType = nodeType;
            _execute = execute;
        }

        public string NodeType { get; }

        public Task<WorkflowNodeOutput> ExecuteAsync(
            WorkflowExecutionContext context,
            WorkflowNode node,
            IReadOnlyCollection<WorkflowNodeOutput> inputs,
            CancellationToken cancellationToken)
            => Task.FromResult(_execute(context, node, inputs));
    }
}
