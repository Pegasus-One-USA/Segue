using FHIRBridge.Application.Abstractions.Audit;
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

    // P2: node-level Operational Log narration + failure logging, sharing this decorator's one wrap point with lineage.
    [Fact]
    public async Task Source_node_records_information_narration_with_fetched_count_on_success()
    {
        var (tracker, _) = CreateTracker();
        var (auditService, recorded) = CreateAuditService();
        var inner = new FakeExecutor(WorkflowNodeTypes.EpicSource, (_, node, _) => new WorkflowNodeOutput(
            node.Id, node.NodeType,
            new ResourceBatch([new ResourceEnvelope("Patient", "p1", "{}"), new ResourceEnvelope("Patient", "p2", "{}")]),
            WorkflowDataContract.ResourceBatch));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object, auditService.Object);
        var node = BuildNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source);

        await decorator.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        recorded.Should().ContainSingle(r =>
            r.Severity == OperationalLogSeverities.Information
            && r.Action == "NodeExecutionCompleted"
            && r.Message.Contains("Fetched 2 resource(s)"));
    }

    [Fact]
    public async Task Destination_node_records_information_narration_with_write_count_on_success()
    {
        var (tracker, _) = CreateTracker();
        var (auditService, recorded) = CreateAuditService();
        var inner = new FakeExecutor(WorkflowNodeTypes.SqlServerDestination, (_, node, _) => new WorkflowNodeOutput(
            node.Id, node.NodeType, new DestinationWriteResult("dest-1", 42, DateTimeOffset.UtcNow),
            WorkflowDataContract.DestinationWriteResult));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object, auditService.Object);
        var node = BuildNode(WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination);

        await decorator.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        recorded.Should().ContainSingle(r =>
            r.Severity == OperationalLogSeverities.Information
            && r.Message.Contains("Wrote 42 row(s) to destination"));
    }

    [Fact]
    public async Task Mapping_node_records_information_narration_with_mapped_record_count_on_success()
    {
        var (tracker, _) = CreateTracker();
        var (auditService, recorded) = CreateAuditService();
        var mappedRecords = new MappedRecordBatch(
            [new MappedDestinationRecord(Guid.NewGuid(), "Patient", "dbo.Patient", "p1", new Dictionary<string, object?>())]);
        var inner = new FakeExecutor(WorkflowNodeTypes.Mapping, (_, node, _) => new WorkflowNodeOutput(
            node.Id, node.NodeType, mappedRecords, WorkflowDataContract.MappedRecordBatch));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object, auditService.Object);
        var node = BuildNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform);

        await decorator.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        recorded.Should().ContainSingle(r => r.Message.Contains("Processed 1 record(s)"));
    }

    [Fact]
    public async Task Analytics_node_records_no_narration_on_success()
    {
        var (tracker, _) = CreateTracker();
        var (auditService, recorded) = CreateAuditService();
        var inner = new FakeExecutor(WorkflowNodeTypes.HedisMeasureReport, (_, node, _) => new WorkflowNodeOutput(
            node.Id, node.NodeType, new ResourceBatch([new ResourceEnvelope("Patient", "p1", "{}")]), WorkflowDataContract.ResourceBatch));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object, auditService.Object);
        var node = BuildNode(WorkflowNodeTypes.HedisMeasureReport, WorkflowNodeCategory.Analytics);

        await decorator.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Node_that_throws_records_an_error_entry_and_rethrows_without_recording_lineage()
    {
        var (tracker, lineageRecorded) = CreateTracker();
        var (auditService, recorded) = CreateAuditService();
        var inner = new FakeExecutor(WorkflowNodeTypes.EpicSource, (_, _, _) =>
            throw new InvalidOperationException("connector unreachable"));
        var decorator = new LineageTrackingWorkflowNodeExecutor(inner, tracker.Object, auditService.Object);
        var node = BuildNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source);

        var act = () => decorator.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("connector unreachable");
        lineageRecorded.Should().BeEmpty();
        recorded.Should().ContainSingle(r =>
            r.Severity == OperationalLogSeverities.Error
            && r.Action == "NodeExecutionFailed"
            && r.Message.Contains("connector unreachable"));
    }

    private static (Mock<IOperationalAuditService> AuditService, List<RecordOperationalAuditLogRequest> Recorded) CreateAuditService()
    {
        var recorded = new List<RecordOperationalAuditLogRequest>();
        var auditService = new Mock<IOperationalAuditService>();
        auditService.Setup(x => x.RecordAsync(It.IsAny<RecordOperationalAuditLogRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RecordOperationalAuditLogRequest, CancellationToken>((request, _) => recorded.Add(request))
            .Returns(Task.CompletedTask);

        return (auditService, recorded);
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
