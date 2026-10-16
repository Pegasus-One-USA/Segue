using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Exceptions;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Every run, bulk or not, from any source, announces its status changes through the one IRunStatusNotifier the host
// registered: SignalR in the API, Redis in the Worker (relayed by the API). These pin that a NON-bulk run's
// announcements — the ones Execution History, the Workflows list and the Dashboard react to — are unchanged by the bulk
// export work: Running, then exactly one terminal status, each only after that status is saved, for every source node
// type. The orchestrator holds no per-vendor branch, so this is the shared path's coverage, not a per-EHR one.
public sealed class RunStatusNotificationTests
{
    // Every source node the runtime catalog can run (DefaultWorkflowNodeCatalog). Cerner, Allscripts, MEDITECH and the
    // HL7 v2 MLLP listener have node-type names but no catalog entry, so a workflow cannot run them as a source node.
    public static TheoryData<string> SourceNodeTypes => new()
    {
        WorkflowNodeTypes.EpicSource,
        WorkflowNodeTypes.EClinicalWorksSource,
        WorkflowNodeTypes.AthenahealthSource,
        WorkflowNodeTypes.GenericFhirSource,
        WorkflowNodeTypes.SampleSource,
        WorkflowNodeTypes.TabularSource,
    };

    /// <summary>The configuration the graph validator requires before a source node may run at all.</summary>
    private static string RequiredConfigurationFor(string sourceNodeType)
        => sourceNodeType == WorkflowNodeTypes.TabularSource
            ? "{\"tab_kind\":\"csv\",\"tab_datasetKey\":\"patients\",\"tab_templates\":\"[]\"}"
            : "{}";

    [Theory]
    [MemberData(nameof(SourceNodeTypes))]
    public async Task A_run_that_succeeds_announces_Running_then_Succeeded(string sourceNodeType)
    {
        var (orchestrator, runStore, notifier, workflow) = Create(sourceNodeType, source: _ => "bundle");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "notify-success");

        var result = await orchestrator.ExecuteAsync(workflow, context);

        result.WorkflowRun.Status.Should().Be(WorkflowRunStatus.Succeeded);
        notifier.Statuses.Should().Equal("Running", "Succeeded");
        notifier.Events.Should().OnlyContain(e =>
            e.Event.WorkflowRunId == context.WorkflowRunId && e.Event.WorkflowDefinitionId == workflow.Id);
        notifier.Events[^1].SavedStatusAtNotifyTime.Should().Be(WorkflowRunStatus.Succeeded);
        runStore.LastSavedStatus(context.WorkflowRunId).Should().Be(WorkflowRunStatus.Succeeded);
    }

    [Theory]
    [MemberData(nameof(SourceNodeTypes))]
    public async Task A_run_whose_source_fails_announces_Failed(string sourceNodeType)
    {
        // E.g. the source's token request refused before any data is read: the run must end Failed, not stay Running.
        var (orchestrator, _, notifier, workflow) = Create(
            sourceNodeType, source: _ => throw new InvalidOperationException("Token request failed: invalid_client"));
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "notify-failure");

        var act = () => orchestrator.ExecuteAsync(workflow, context);

        await act.Should().ThrowAsync<InvalidOperationException>();
        notifier.Statuses.Should().Equal("Running", "Failed");
        notifier.Events[^1].Event.ErrorMessage.Should().Contain("invalid_client");
        notifier.Events[^1].SavedStatusAtNotifyTime.Should().Be(WorkflowRunStatus.Failed);
    }

    [Theory]
    [MemberData(nameof(SourceNodeTypes))]
    public async Task A_run_cancelled_for_an_unauthorized_parent_type_announces_Cancelled(string sourceNodeType)
    {
        // E.g. a missing OAuth scope for Patient (403): the run is cancelled up front.
        var (orchestrator, _, notifier, workflow) = Create(sourceNodeType, source: _ =>
            throw new WorkflowRunCancelledException("Patient", "403 Forbidden", new InvalidOperationException("403 Forbidden")));
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "notify-cancel");

        var act = () => orchestrator.ExecuteAsync(workflow, context);

        await act.Should().ThrowAsync<WorkflowRunCancelledException>();
        notifier.Statuses.Should().Equal("Running", "Cancelled");
        notifier.Events[^1].SavedStatusAtNotifyTime.Should().Be(WorkflowRunStatus.Cancelled);
    }

    [Fact]
    public async Task A_run_with_skipped_child_types_announces_PartialSuccess()
    {
        var (orchestrator, _, notifier, workflow) = Create(
            WorkflowNodeTypes.EpicSource,
            source: _ => "bundle",
            sourceMetadata: new Dictionary<string, object?>
            {
                ["skippedResourceTypes"] = new[] { "Observation: not authorized for this app (403) — Forbidden" },
            });
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "notify-partial");

        await orchestrator.ExecuteAsync(workflow, context);

        notifier.Statuses.Should().Equal("Running", "PartialSuccess");
        notifier.Events[^1].SavedStatusAtNotifyTime.Should().Be(WorkflowRunStatus.PartialSuccess);
    }

    private static (RankedWorkflowOrchestrator Orchestrator, SaveRecordingRunStore RunStore, RecordingNotifier Notifier, WorkflowDefinition Workflow)
        Create(
            string sourceNodeType,
            Func<IReadOnlyCollection<WorkflowNodeOutput>, object?> source,
            IReadOnlyDictionary<string, object?>? sourceMetadata = null)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), $"notify-{sourceNodeType}", 1);
        var sourceNode = workflow.AddNode(sourceNodeType, WorkflowNodeCategory.Source, 0, configurationJson: RequiredConfigurationFor(sourceNodeType));
        var mapping = workflow.AddNode(WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60, configurationJson: "{}");
        var destination = workflow.AddNode(WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70, configurationJson: "{}");
        workflow.AddEdge(sourceNode.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, destination.Id);

        var runStore = new SaveRecordingRunStore();
        var notifier = new RecordingNotifier(runStore);
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new IWorkflowNodeExecutor[]
            {
                new Executor(sourceNodeType, WorkflowDataContract.ResourceBatch, source, sourceMetadata),
                new Executor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
                new Executor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs => inputs.Single().Payload),
            }),
            auditRecorder: null,
            runStore: runStore,
            runStatusNotifier: notifier);
        return (orchestrator, runStore, notifier, workflow);
    }

    private sealed class Executor : IWorkflowNodeExecutor
    {
        private readonly WorkflowDataContract _contract;
        private readonly Func<IReadOnlyCollection<WorkflowNodeOutput>, object?> _execute;
        private readonly IReadOnlyDictionary<string, object?>? _metadata;

        public Executor(
            string nodeType,
            WorkflowDataContract contract,
            Func<IReadOnlyCollection<WorkflowNodeOutput>, object?> execute,
            IReadOnlyDictionary<string, object?>? metadata = null)
        {
            NodeType = nodeType;
            _contract = contract;
            _execute = execute;
            _metadata = metadata;
        }

        public string NodeType { get; }

        public Task<WorkflowNodeOutput> ExecuteAsync(
            WorkflowExecutionContext context,
            WorkflowNode node,
            IReadOnlyCollection<WorkflowNodeOutput> inputs,
            CancellationToken cancellationToken)
            => Task.FromResult(new WorkflowNodeOutput(node.Id, node.NodeType, _execute(inputs), _contract, _metadata));
    }

    private sealed class RecordingNotifier : IRunStatusNotifier
    {
        private readonly SaveRecordingRunStore _runStore;

        public RecordingNotifier(SaveRecordingRunStore runStore)
        {
            _runStore = runStore;
        }

        public List<(RunStatusChangedEvent Event, WorkflowRunStatus? SavedStatusAtNotifyTime)> Events { get; } = [];

        public IEnumerable<string> Statuses => Events.Select(e => e.Event.Status);

        public Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken)
        {
            Events.Add((statusEvent, _runStore.LastSavedStatus(statusEvent.WorkflowRunId)));
            return Task.CompletedTask;
        }
    }
}
