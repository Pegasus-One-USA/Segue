using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Audit;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
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
        // Uses two enabled sources (Epic + Sample) and two enabled destinations (SqlServer + CSV) for the fan-out /
        // fan-in check; the specific vendors are incidental — the gated catalog only exposes these in the SQL/CSV phase.
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "fan-in-out", 1);
        var epic = AddNode(workflow, WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 0);
        var sample = AddNode(workflow, WorkflowNodeTypes.SampleSource, WorkflowNodeCategory.Source, 0);
        var consent = AddNode(workflow, WorkflowNodeTypes.Consent, WorkflowNodeCategory.Compliance, 10);
        var usCore = AddNode(workflow, WorkflowNodeTypes.UsCoreValidation, WorkflowNodeCategory.Compliance, 20);
        var normalization = AddNode(workflow, WorkflowNodeTypes.Normalization, WorkflowNodeCategory.Transform, 30);
        var terminology = AddNode(workflow, WorkflowNodeTypes.Terminology, WorkflowNodeCategory.Transform, 40);
        var deIdentification = AddNode(workflow, WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50);
        var mapping = AddNode(workflow, WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60);
        var sql = AddNode(workflow, WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        var csv = AddNode(workflow, WorkflowNodeTypes.CsvDestination, WorkflowNodeCategory.Destination, 70);
        workflow.AddEdge(epic.Id, consent.Id);
        workflow.AddEdge(sample.Id, consent.Id);
        workflow.AddEdge(consent.Id, usCore.Id);
        workflow.AddEdge(usCore.Id, normalization.Id);
        workflow.AddEdge(normalization.Id, terminology.Id);
        workflow.AddEdge(terminology.Id, deIdentification.Id);
        workflow.AddEdge(deIdentification.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, sql.Id);
        workflow.AddEdge(mapping.Id, csv.Id);

        var destinationInputs = Array.Empty<WorkflowNodeOutput>();
        var csvInputs = Array.Empty<WorkflowNodeOutput>();
        var orchestrator = CreateOrchestrator(
            new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "epic"),
            new PayloadExecutor(WorkflowNodeTypes.SampleSource, WorkflowDataContract.ResourceBatch, _ => "sample"),
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
            new PayloadExecutor(WorkflowNodeTypes.CsvDestination, WorkflowDataContract.DestinationWriteResult, inputs =>
            {
                csvInputs = inputs.ToArray();
                return string.Join(",", inputs.Select(input => input.Payload));
            }));

        var result = await orchestrator.ExecuteAsync(workflow, CreateContext());

        destinationInputs.Select(input => input.Payload).Should().Equal("mapping:deid:term:norm:uscore:consent:epic+sample");
        csvInputs.Select(input => input.Payload).Should().Equal("mapping:deid:term:norm:uscore:consent:epic+sample");
        result.OutputsByNodeId[sql.Id].Payload.Should().Be("mapping:deid:term:norm:uscore:consent:epic+sample");
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

    [Fact]
    public async Task ExecuteAsync_persists_succeeded_run_to_the_run_store()
    {
        var workflow = BuildValidSourceToSqlWorkflow("persisted-success");
        var runStore = new InMemoryWorkflowRunStore();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new[]
            {
                new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "bundle"),
                new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs => inputs.Single().Payload)
            }),
            auditRecorder: null,
            runStore: runStore);
        var context = CreateContext();

        await orchestrator.ExecuteAsync(workflow, context);

        var persisted = await runStore.GetAsync(context.WorkflowRunId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(WorkflowRunStatus.Succeeded);
        persisted.WorkflowDefinitionId.Should().Be(workflow.Id);
        persisted.NodeRuns.Should().HaveCount(4);
        persisted.CorrelationId.Should().Be(context.CorrelationId);
    }

    [Fact]
    public async Task ExecuteAsync_persists_failed_run_to_the_run_store_before_rethrowing()
    {
        var workflow = BuildValidSourceToSqlWorkflow("persisted-failure");
        var runStore = new InMemoryWorkflowRunStore();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new[]
            {
                new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "bundle"),
                new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, _ => throw new InvalidOperationException("write failed"))
            }),
            auditRecorder: null,
            runStore: runStore);
        var context = CreateContext();

        var act = async () => await orchestrator.ExecuteAsync(workflow, context);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var persisted = await runStore.GetAsync(context.WorkflowRunId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(WorkflowRunStatus.Failed);
        persisted.ErrorMessage.Should().Be("write failed");
    }

    [Fact]
    public async Task ExecuteAsync_captures_failed_run_via_exception_manager_with_matching_correlation_id()
    {
        var workflow = BuildValidSourceToSqlWorkflow("captured-failure");
        var exceptionManager = new RecordingExceptionManager();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new[]
            {
                new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "bundle"),
                new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, _ => throw new InvalidOperationException("write failed"))
            }),
            auditRecorder: null,
            runStore: null,
            exceptionManager: exceptionManager);
        var context = CreateContext();

        var act = async () => await orchestrator.ExecuteAsync(workflow, context);

        await act.Should().ThrowAsync<InvalidOperationException>();
        exceptionManager.CapturedContexts.Should().ContainSingle();
        exceptionManager.CapturedContexts[0].CorrelationId.Should().Be(context.CorrelationId);
        exceptionManager.CapturedContexts[0].WorkflowId.Should().Be(workflow.Id.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_with_targetNodeId_runs_only_the_ancestor_closure_not_sibling_branches()
    {
        // Fan-out: Mapping feeds both SqlServer and Csv destinations. A checkpoint targeting SqlServer must run
        // Mapping (its ancestor) but must NEVER run Csv (a sibling, not an ancestor) — this is the exact scenario
        // rank-threshold filtering would get wrong (see docs/backend/05-workflow-node-checkpoints-plan.md §3.4).
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "checkpoint-fan-out", 1);
        var source = AddNode(workflow, WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 0);
        var mapping = AddNode(workflow, WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60);
        var sql = AddNode(workflow, WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        var csv = AddNode(workflow, WorkflowNodeTypes.CsvDestination, WorkflowNodeCategory.Destination, 70);
        workflow.AddEdge(source.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, sql.Id);
        workflow.AddEdge(mapping.Id, csv.Id);

        var calls = new List<string>();
        var orchestrator = CreateOrchestrator(
            new RecordingExecutor(WorkflowNodeTypes.EpicSource, calls, WorkflowDataContract.ResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.Mapping, calls, WorkflowDataContract.MappedRecordBatch),
            new RecordingExecutor(WorkflowNodeTypes.SqlServerDestination, calls, WorkflowDataContract.DestinationWriteResult),
            new RecordingExecutor(WorkflowNodeTypes.CsvDestination, calls, WorkflowDataContract.DestinationWriteResult));

        var result = await orchestrator.ExecuteAsync(workflow, CreateContext(), targetNodeId: sql.Id);

        calls.Should().Equal(WorkflowNodeTypes.EpicSource, WorkflowNodeTypes.Mapping, WorkflowNodeTypes.SqlServerDestination);
        calls.Should().NotContain(WorkflowNodeTypes.CsvDestination);
        result.WorkflowRun.TargetNodeId.Should().Be(sql.Id);
        result.WorkflowRun.NodeRuns.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_with_targetNodeId_on_a_diamond_still_includes_every_ancestor_branch()
    {
        // Diamond: two independent upstream branches (Epic, Sample) merge — via separate Consent nodes — into one
        // Mapping node. A checkpoint on the destination must include BOTH branches, not just one.
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "checkpoint-diamond", 1);
        var epic = AddNode(workflow, WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 0);
        var sample = AddNode(workflow, WorkflowNodeTypes.SampleSource, WorkflowNodeCategory.Source, 0);
        var epicConsent = AddNode(workflow, WorkflowNodeTypes.Consent, WorkflowNodeCategory.Compliance, 10);
        var sampleConsent = AddNode(workflow, WorkflowNodeTypes.Consent, WorkflowNodeCategory.Compliance, 10);
        var mapping = AddNode(workflow, WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60);
        var sql = AddNode(workflow, WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        workflow.AddEdge(epic.Id, epicConsent.Id);
        workflow.AddEdge(sample.Id, sampleConsent.Id);
        workflow.AddEdge(epicConsent.Id, mapping.Id);
        workflow.AddEdge(sampleConsent.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, sql.Id);

        var calls = new List<string>();
        var orchestrator = CreateOrchestrator(
            new RecordingExecutor(WorkflowNodeTypes.EpicSource, calls, WorkflowDataContract.ResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.SampleSource, calls, WorkflowDataContract.ResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.Consent, calls, WorkflowDataContract.ResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.Mapping, calls, WorkflowDataContract.MappedRecordBatch),
            new RecordingExecutor(WorkflowNodeTypes.SqlServerDestination, calls, WorkflowDataContract.DestinationWriteResult));

        var result = await orchestrator.ExecuteAsync(workflow, CreateContext(), targetNodeId: sql.Id);

        result.WorkflowRun.NodeRuns.Should().HaveCount(6);
        calls.Count(call => call == WorkflowNodeTypes.Consent).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_with_targetNodeId_that_does_not_exist_throws()
    {
        var workflow = BuildValidSourceToSqlWorkflow("checkpoint-missing-node");
        var orchestrator = CreateOrchestrator(
            new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "bundle"),
            new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => inputs.Single().Payload),
            new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
            new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs => inputs.Single().Payload));

        var act = async () => await orchestrator.ExecuteAsync(workflow, CreateContext(), targetNodeId: Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_cancels_the_run_when_a_node_reports_the_parent_resource_type_is_unauthorized()
    {
        var workflow = BuildValidSourceToSqlWorkflow("cancelled-parent");
        var runStore = new InMemoryWorkflowRunStore();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new IWorkflowNodeExecutor[]
            {
                new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ =>
                    throw new FHIRBridge.Runtime.Domain.Exceptions.WorkflowRunCancelledException(
                        "Patient", "403 Forbidden", new InvalidOperationException("403 Forbidden"))),
                new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs => inputs.Single().Payload)
            }),
            auditRecorder: null,
            runStore: runStore);
        var context = CreateContext();

        var act = async () => await orchestrator.ExecuteAsync(workflow, context);

        await act.Should().ThrowAsync<FHIRBridge.Runtime.Domain.Exceptions.WorkflowRunCancelledException>();
        var persisted = await runStore.GetAsync(context.WorkflowRunId, CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(WorkflowRunStatus.Cancelled);
        persisted.ErrorMessage.Should().Contain("Patient");
        persisted.CorrelationId.Should().Be(context.CorrelationId);
    }

    [Fact]
    public async Task ExecuteAsync_marks_the_run_PartialSuccess_when_a_node_reports_skipped_child_resource_types()
    {
        var workflow = BuildValidSourceToSqlWorkflow("partial-success");
        var runStore = new InMemoryWorkflowRunStore();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new IWorkflowNodeExecutor[]
            {
                new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "bundle",
                    metadata: new Dictionary<string, object?>
                    {
                        ["skippedResourceTypes"] = new[] { "Observation: not authorized for this app (403) — Forbidden" }
                    }),
                new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs => inputs.Single().Payload)
            }),
            auditRecorder: null,
            runStore: runStore);
        var context = CreateContext();

        var result = await orchestrator.ExecuteAsync(workflow, context);

        result.WorkflowRun.Status.Should().Be(WorkflowRunStatus.PartialSuccess);
        result.WorkflowRun.ErrorMessage.Should().Contain("Observation");
        var persisted = await runStore.GetAsync(context.WorkflowRunId, CancellationToken.None);
        persisted!.Status.Should().Be(WorkflowRunStatus.PartialSuccess);
        persisted.CorrelationId.Should().Be(context.CorrelationId);
    }

    [Fact]
    public async Task ExecuteAsync_captures_partial_success_via_exception_manager_so_it_shows_in_correlation_search()
    {
        // PartialSuccess is a normal completion path (no exception thrown), so unlike Cancel/Fail it was never
        // routed through GlobalExceptionManager — the skipped-resource-type reason only ever landed in
        // WorkflowRun.ErrorMessage, which Correlation Search's Workflow Runs table doesn't render at all. This
        // capture makes it show up in the Errors section instead, the same place Cancel's reason already does.
        var workflow = BuildValidSourceToSqlWorkflow("partial-success-captured");
        var exceptionManager = new RecordingExceptionManager();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new IWorkflowNodeExecutor[]
            {
                new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "bundle",
                    metadata: new Dictionary<string, object?>
                    {
                        ["skippedResourceTypes"] = new[] { "Observation: not authorized for this app (403) — Forbidden" }
                    }),
                new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
                new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs => inputs.Single().Payload)
            }),
            auditRecorder: null,
            runStore: null,
            exceptionManager: exceptionManager);
        var context = CreateContext();

        var result = await orchestrator.ExecuteAsync(workflow, context);

        result.WorkflowRun.Status.Should().Be(WorkflowRunStatus.PartialSuccess);
        exceptionManager.CapturedContexts.Should().ContainSingle();
        exceptionManager.CapturedContexts[0].CorrelationId.Should().Be(context.CorrelationId);
        exceptionManager.CapturedContexts[0].WorkflowId.Should().Be(workflow.Id.ToString());
        exceptionManager.CapturedContexts[0].ExecutionId.Should().Be(context.WorkflowRunId.ToString());
        exceptionManager.CapturedContexts[0].Severity.Should().Be("Informational");
    }

    [Fact]
    public async Task ExecuteAsync_pauses_when_a_source_node_defers_to_a_bulk_export_job()
    {
        var workflow = BuildValidSourceToSqlWorkflow("bulk-export-pause");
        var runStore = new InMemoryWorkflowRunStore();
        var pauseRecorder = new RecordingBulkExportPauseRecorder();
        var deferredJobId = Guid.NewGuid();
        var calls = new List<string>();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new IWorkflowNodeExecutor[]
            {
                new DeferringExecutor(WorkflowNodeTypes.EpicSource, deferredJobId),
                new RecordingExecutor(WorkflowNodeTypes.DeIdentification, calls, WorkflowDataContract.DeIdentifiedBatch),
                new RecordingExecutor(WorkflowNodeTypes.Mapping, calls, WorkflowDataContract.MappedRecordBatch),
                new RecordingExecutor(WorkflowNodeTypes.SqlServerDestination, calls, WorkflowDataContract.DestinationWriteResult)
            }),
            auditRecorder: null,
            runStore: runStore,
            bulkExportPauseRecorder: pauseRecorder);
        var context = CreateContext();

        var result = await orchestrator.ExecuteAsync(workflow, context);

        result.WorkflowRun.Status.Should().Be(WorkflowRunStatus.AwaitingBulkExport);
        result.WorkflowRun.NodeRuns.Should().BeEmpty(); // the deferring node's run isn't recorded until resume
        calls.Should().BeEmpty(); // nothing downstream of the deferring source node ran
        pauseRecorder.RecordedJobId.Should().Be(deferredJobId);
        pauseRecorder.RecordedPriorOutputsJson.Should().Be("{}"); // nothing computed before the first node

        var persisted = await runStore.GetAsync(context.WorkflowRunId, CancellationToken.None);
        persisted!.Status.Should().Be(WorkflowRunStatus.AwaitingBulkExport);
    }

    [Fact]
    public async Task ResumeAfterBulkExportAsync_continues_from_the_node_after_the_paused_source_and_completes_the_run()
    {
        var workflow = BuildValidSourceToSqlWorkflow("bulk-export-resume");
        var sourceNodeId = workflow.Nodes.Single(n => n.NodeType == WorkflowNodeTypes.EpicSource).Id;
        var definitionStore = new StubWorkflowDefinitionStore(workflow);
        var runStore = new InMemoryWorkflowRunStore();
        var calls = new List<string>();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new IWorkflowNodeExecutor[]
            {
                new RecordingExecutor(WorkflowNodeTypes.EpicSource, calls, WorkflowDataContract.ResourceBatch),
                new RecordingExecutor(WorkflowNodeTypes.DeIdentification, calls, WorkflowDataContract.DeIdentifiedBatch),
                new RecordingExecutor(WorkflowNodeTypes.Mapping, calls, WorkflowDataContract.MappedRecordBatch),
                new RecordingExecutor(WorkflowNodeTypes.SqlServerDestination, calls, WorkflowDataContract.DestinationWriteResult)
            }),
            auditRecorder: null,
            runStore: runStore,
            workflowDefinitionStore: definitionStore);

        // Seed a paused run exactly as the pause path in ExecuteAsync would have left it persisted.
        var pausedRun = new WorkflowRun(Guid.NewGuid(), workflow.Id, DateTimeOffset.UtcNow, correlationId: "resume-test");
        pausedRun.AwaitBulkExport();
        await runStore.SaveAsync(pausedRun, CancellationToken.None);

        var resources = new[]
        {
            new FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope("Patient", "p1", "{\"resourceType\":\"Patient\"}", null, null)
        };

        var result = await orchestrator.ResumeAfterBulkExportAsync(
            pausedRun.Id, sourceNodeId, priorNodeOutputsJson: null, contextJson: null, resources,
            skippedResourceTypeReasons: null, cancellationToken: CancellationToken.None);

        calls.Should().Equal(WorkflowNodeTypes.DeIdentification, WorkflowNodeTypes.Mapping, WorkflowNodeTypes.SqlServerDestination);
        result.WorkflowRun.Status.Should().Be(WorkflowRunStatus.Succeeded);
        result.OutputsByNodeId[sourceNodeId].Contract.Should().Be(WorkflowDataContract.ResourceBatch);

        var persisted = await runStore.GetAsync(pausedRun.Id, CancellationToken.None);
        persisted!.Status.Should().Be(WorkflowRunStatus.Succeeded);
    }

    /// <summary>
    /// Executes the exact node/rank/edge shape of the production workflow "Epic | ehrLaunch | PatientOnly"
    /// (WorkflowDefinitions.Id 8C8CFA21-01EF-4C7D-834F-2E22CC367642) through the real orchestrator end to end —
    /// EpicSourceNode (rank 0) -&gt; MappingNode (rank 60) -&gt; SqlServerDestinationNode (rank 70) — with fake node
    /// executors standing in for the real Epic connector / SQL Server writer (neither is reachable from a unit
    /// test: the real run needs a live Epic OAuth handshake and a live SQL Server). Confirms the DAG itself
    /// completes successfully — every node runs exactly once, in order, and the run finishes Succeeded — the same
    /// check RankedWorkflowOrchestrator performs for a real run via PipelineOrchestrator.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_runs_the_production_epic_ehr_launch_patient_only_workflow_to_completion()
    {
        var workflow = new WorkflowDefinition(Guid.Parse("8C8CFA21-01EF-4C7D-834F-2E22CC367642"), "Epic | ehrLaunch | PatientOnly", 1);
        var source = AddNode(workflow, WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 0);
        var mapping = AddNode(workflow, WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60);
        var destination = AddNode(workflow, WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        workflow.AddEdge(source.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, destination.Id);

        var calls = new List<string>();
        var orchestrator = CreateOrchestrator(
            new RecordingExecutor(WorkflowNodeTypes.EpicSource, calls, WorkflowDataContract.ResourceBatch),
            new RecordingExecutor(WorkflowNodeTypes.Mapping, calls, WorkflowDataContract.MappedRecordBatch),
            new RecordingExecutor(WorkflowNodeTypes.SqlServerDestination, calls, WorkflowDataContract.DestinationWriteResult));

        var result = await orchestrator.ExecuteAsync(workflow, CreateContext());

        calls.Should().Equal(WorkflowNodeTypes.EpicSource, WorkflowNodeTypes.Mapping, WorkflowNodeTypes.SqlServerDestination);
        result.WorkflowRun.Status.Should().Be(WorkflowRunStatus.Succeeded);
        result.WorkflowRun.NodeRuns.Should().HaveCount(3);
        result.WorkflowRun.NodeRuns.Should().OnlyContain(nodeRun => nodeRun.Status == WorkflowRunStatus.Succeeded);
    }

    // ---- PR #244 re-review ---------------------------------------------------------------------------------------

    private static IWorkflowNodeExecutor[] PassThroughExecutors(Action? onDeIdentification = null) =>
    [
        new PayloadExecutor(WorkflowNodeTypes.EpicSource, WorkflowDataContract.ResourceBatch, _ => "bundle"),
        new PayloadExecutor(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, inputs =>
        {
            onDeIdentification?.Invoke();
            return inputs.Single().Payload;
        }),
        new PayloadExecutor(WorkflowNodeTypes.Mapping, WorkflowDataContract.MappedRecordBatch, inputs => inputs.Single().Payload),
        new PayloadExecutor(WorkflowNodeTypes.SqlServerDestination, WorkflowDataContract.DestinationWriteResult, inputs => inputs.Single().Payload),
    ];

    [Fact]
    public async Task A_run_another_instance_closed_keeps_the_recorded_outcome_and_is_not_failed_here()
    {
        var workflow = BuildValidSourceToSqlWorkflow("closed-elsewhere");
        var audit = new InMemoryWorkflowAuditRecorder();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors()),
            auditRecorder: audit,
            runStore: new ClosedElsewhereRunStore());

        var result = await orchestrator.ExecuteAsync(workflow, CreateContext());

        result.WorkflowRun.Status.Should().Be(WorkflowRunStatus.Succeeded, because: "Fail is never called on a run that succeeded");
        audit.Events.Should().NotContain(e => e.EventType == WorkflowAuditEventType.WorkflowRunFailed);
    }

    // PR #244 review: the cancel and failure branches notify the status another instance recorded, not their own.
    [Fact]
    public async Task A_failure_on_a_run_another_instance_closed_notifies_the_recorded_status()
    {
        var workflow = BuildValidSourceToSqlWorkflow("fail-closed-elsewhere");
        var notifier = new RecordingRunStatusNotifier();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors(() => throw new InvalidOperationException("boom"))),
            runStore: new ClosedElsewhereRunStore(nameof(WorkflowRunStatus.Cancelled)),
            runStatusNotifier: notifier);

        var act = () => orchestrator.ExecuteAsync(workflow, CreateContext());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        var terminal = notifier.Events.Last();
        terminal.Status.Should().Be(nameof(WorkflowRunStatus.Cancelled));
        terminal.ErrorMessage.Should().BeNull();
        notifier.Events.Should().NotContain(e => e.Status == nameof(WorkflowRunStatus.Failed));
    }

    [Fact]
    public async Task A_cancel_on_a_run_another_instance_closed_notifies_the_recorded_status()
    {
        // The lease heartbeat cancels a run another instance already failed as interrupted.
        var workflow = BuildValidSourceToSqlWorkflow("cancel-closed-elsewhere");
        var notifier = new RecordingRunStatusNotifier();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors(() => throw new OperationCanceledException())),
            runStore: new ClosedElsewhereRunStore(nameof(WorkflowRunStatus.Failed)),
            runStatusNotifier: notifier);

        await orchestrator.ExecuteAsync(workflow, CreateContext());

        var terminal = notifier.Events.Last();
        terminal.Status.Should().Be(nameof(WorkflowRunStatus.Failed));
        terminal.ErrorMessage.Should().BeNull("'Cancelled by user request.' is not what happened to this run");
        notifier.Events.Should().NotContain(e => e.Status == nameof(WorkflowRunStatus.Cancelled));
    }

    // PR #244 review: a run another instance closed with the SAME status this branch reached still holds that
    // instance's reason, so the push must not carry this branch's message or reference id.
    [Fact]
    public async Task A_failure_on_a_run_another_instance_already_failed_pushes_no_message_or_reference()
    {
        var workflow = BuildValidSourceToSqlWorkflow("fail-failed-elsewhere");
        var notifier = new RecordingRunStatusNotifier();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors(() => throw new InvalidOperationException("boom"))),
            runStore: new ClosedElsewhereRunStore(nameof(WorkflowRunStatus.Failed)),
            exceptionManager: new RecordingExceptionManager(),
            runStatusNotifier: notifier);

        var act = () => orchestrator.ExecuteAsync(workflow, CreateContext());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        var terminal = notifier.Events.Last();
        terminal.Status.Should().Be(nameof(WorkflowRunStatus.Failed));
        terminal.ErrorMessage.Should().BeNull("the row holds the other instance's interrupted reason, not 'boom'");
        terminal.ErrorReferenceId.Should().BeNull();
    }

    [Fact]
    public async Task A_cancel_on_a_run_another_instance_already_cancelled_pushes_no_message_or_reference()
    {
        var workflow = BuildValidSourceToSqlWorkflow("cancel-cancelled-elsewhere");
        var notifier = new RecordingRunStatusNotifier();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors(() => throw new OperationCanceledException())),
            runStore: new ClosedElsewhereRunStore(nameof(WorkflowRunStatus.Cancelled)),
            exceptionManager: new RecordingExceptionManager(),
            runStatusNotifier: notifier);

        await orchestrator.ExecuteAsync(workflow, CreateContext());

        var terminal = notifier.Events.Last();
        terminal.Status.Should().Be(nameof(WorkflowRunStatus.Cancelled));
        terminal.ErrorMessage.Should().BeNull("the run was cancelled as abandoned elsewhere, not by user request");
        terminal.ErrorReferenceId.Should().BeNull();
    }

    [Fact]
    public async Task A_failure_that_saves_pushes_its_message_and_reference()
    {
        var workflow = BuildValidSourceToSqlWorkflow("fail-saved");
        var notifier = new RecordingRunStatusNotifier();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors(() => throw new InvalidOperationException("boom"))),
            runStore: new InMemoryWorkflowRunStore(),
            exceptionManager: new RecordingExceptionManager(),
            runStatusNotifier: notifier);

        var act = () => orchestrator.ExecuteAsync(workflow, CreateContext());

        await act.Should().ThrowAsync<InvalidOperationException>();
        var terminal = notifier.Events.Last();
        terminal.Status.Should().Be(nameof(WorkflowRunStatus.Failed));
        terminal.ErrorMessage.Should().Be("boom");
        terminal.ErrorReferenceId.Should().Be("ERR-TEST-000001");
    }

    [Fact]
    public async Task A_cancel_that_saves_notifies_cancelled_with_its_reason()
    {
        var workflow = BuildValidSourceToSqlWorkflow("cancel-saved");
        var notifier = new RecordingRunStatusNotifier();
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors(() => throw new OperationCanceledException())),
            runStore: new InMemoryWorkflowRunStore(),
            runStatusNotifier: notifier);

        await orchestrator.ExecuteAsync(workflow, CreateContext());

        var terminal = notifier.Events.Last();
        terminal.Status.Should().Be(nameof(WorkflowRunStatus.Cancelled));
        terminal.ErrorMessage.Should().Be("Cancelled by user request.");
    }

    private sealed class RecordingRunStatusNotifier : IRunStatusNotifier
    {
        public List<RunStatusChangedEvent> Events { get; } = [];

        public Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken)
        {
            Events.Add(statusEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_resumed_run_is_running_again_under_this_processs_lease()
    {
        var workflow = BuildValidSourceToSqlWorkflow("resume-lease");
        var sourceNodeId = workflow.Nodes.Single(n => n.NodeType == WorkflowNodeTypes.EpicSource).Id;
        var runStore = new InMemoryWorkflowRunStore();
        var active = new ActiveWorkflowRuns();
        var pausedRun = new WorkflowRun(Guid.NewGuid(), workflow.Id, DateTimeOffset.UtcNow, correlationId: "resume-lease");
        pausedRun.AwaitBulkExport();
        await runStore.SaveAsync(pausedRun, CancellationToken.None);
        WorkflowRunStatus? statusWhileRunning = null;
        string? ownerWhileRunning = null;
        var heldWhileRunning = false;
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors(() =>
            {
                statusWhileRunning = pausedRun.Status;
                ownerWhileRunning = pausedRun.LeaseOwner;
                heldWhileRunning = active.Snapshot().Contains(pausedRun.Id);
            })),
            runStore: runStore,
            workflowDefinitionStore: new StubWorkflowDefinitionStore(workflow),
            activeRuns: active);

        await orchestrator.ResumeAfterBulkExportAsync(
            pausedRun.Id, sourceNodeId, priorNodeOutputsJson: null, contextJson: null,
            [new FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope("Patient", "p1", "{\"resourceType\":\"Patient\"}", null, null)],
            cancellationToken: CancellationToken.None);

        statusWhileRunning.Should().Be(WorkflowRunStatus.Running, because: "the heartbeat renews (and fences) only a Running row");
        ownerWhileRunning.Should().Be(active.InstanceId);
        heldWhileRunning.Should().BeTrue();
    }

    [Fact]
    public async Task A_resume_that_fails_before_its_nodes_run_fails_the_run_with_the_real_error()
    {
        var workflow = BuildValidSourceToSqlWorkflow("resume-bad-context");
        var sourceNodeId = workflow.Nodes.Single(n => n.NodeType == WorkflowNodeTypes.EpicSource).Id;
        var runStore = new InMemoryWorkflowRunStore();
        var pausedRun = new WorkflowRun(Guid.NewGuid(), workflow.Id, DateTimeOffset.UtcNow);
        pausedRun.AwaitBulkExport();
        await runStore.SaveAsync(pausedRun, CancellationToken.None);
        var nodeRan = false;
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(PassThroughExecutors(() => nodeRan = true)),
            runStore: runStore,
            workflowDefinitionStore: new StubWorkflowDefinitionStore(workflow),
            activeRuns: new ActiveWorkflowRuns());

        var act = () => orchestrator.ResumeAfterBulkExportAsync(
            pausedRun.Id, sourceNodeId, priorNodeOutputsJson: null, contextJson: "{ not json",
            [], cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
        nodeRan.Should().BeFalse();
        var saved = await runStore.GetAsync(pausedRun.Id, CancellationToken.None);
        saved!.Status.Should().Be(WorkflowRunStatus.Failed, because: "left Running, the expiry sweep would close it as interrupted");
        saved.ErrorMessage.Should().NotBe(WorkflowRunLease.InterruptedReason);
    }

    [Fact]
    public async Task A_paused_run_closed_elsewhere_is_not_resumed()
    {
        var workflow = BuildValidSourceToSqlWorkflow("resume-closed");
        var sourceNodeId = workflow.Nodes.Single(n => n.NodeType == WorkflowNodeTypes.EpicSource).Id;
        var runStore = new InMemoryWorkflowRunStore();
        var calls = new List<string>();
        var closed = new WorkflowRun(Guid.NewGuid(), workflow.Id, DateTimeOffset.UtcNow);
        closed.Cancel("Cancelled: abandoned", DateTimeOffset.UtcNow);
        await runStore.SaveAsync(closed, CancellationToken.None);
        var orchestrator = new RankedWorkflowOrchestrator(
            new WorkflowGraphValidator(),
            new WorkflowNodeExecutorRegistry(new IWorkflowNodeExecutor[]
            {
                new RecordingExecutor(WorkflowNodeTypes.EpicSource, calls, WorkflowDataContract.ResourceBatch),
                new RecordingExecutor(WorkflowNodeTypes.DeIdentification, calls, WorkflowDataContract.DeIdentifiedBatch),
                new RecordingExecutor(WorkflowNodeTypes.Mapping, calls, WorkflowDataContract.MappedRecordBatch),
                new RecordingExecutor(WorkflowNodeTypes.SqlServerDestination, calls, WorkflowDataContract.DestinationWriteResult),
            }),
            runStore: runStore,
            workflowDefinitionStore: new StubWorkflowDefinitionStore(workflow));

        var act = () => orchestrator.ResumeAfterBulkExportAsync(
            closed.Id, sourceNodeId, priorNodeOutputsJson: null, contextJson: null, [], cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowRunAlreadyClosedException>();
        calls.Should().BeEmpty();
        closed.Status.Should().Be(WorkflowRunStatus.Cancelled);
    }

    /// <summary>Refuses every terminal save, as the SQL store does when another instance closed the run first.</summary>
    private sealed class ClosedElsewhereRunStore : IWorkflowRunStore
    {
        private readonly InMemoryWorkflowRunStore _inner = new();
        private readonly string _recordedStatus;

        public ClosedElsewhereRunStore(string recordedStatus = nameof(WorkflowRunStatus.Failed))
        {
            _recordedStatus = recordedStatus;
        }

        public Task SaveAsync(WorkflowRun workflowRun, CancellationToken cancellationToken) =>
            WorkflowRunStatuses.IsTerminal(workflowRun.Status)
                ? throw new WorkflowRunAlreadyClosedException(workflowRun.Id, _recordedStatus)
                : _inner.SaveAsync(workflowRun, cancellationToken);

        public Task<WorkflowRun?> GetAsync(Guid workflowRunId, CancellationToken cancellationToken) => _inner.GetAsync(workflowRunId, cancellationToken);

        public Task<int> ExpireStaleValidatedAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken) =>
            _inner.ExpireStaleValidatedAsync(olderThanUtc, cancellationToken);

        public Task<WorkflowLeaseRenewal> RenewLeasesAsync(IReadOnlyCollection<Guid> runIds, string owner, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            _inner.RenewLeasesAsync(runIds, owner, expiresAt, cancellationToken);

        public Task<int> FailExpiredRunsAsync(DateTimeOffset now, DateTimeOffset legacyStartedBefore, string reason, CancellationToken cancellationToken) =>
            _inner.FailExpiredRunsAsync(now, legacyStartedBefore, reason, cancellationToken);

        public Task<bool> RequestCancellationAsync(Guid workflowRunId, DateTimeOffset requestedAt, CancellationToken cancellationToken) =>
            _inner.RequestCancellationAsync(workflowRunId, requestedAt, cancellationToken);

        public Task<bool> CancelAbandonedAsync(Guid workflowRunId, DateTimeOffset now, DateTimeOffset legacyStartedBefore, string reason, CancellationToken cancellationToken) =>
            _inner.CancelAbandonedAsync(workflowRunId, now, legacyStartedBefore, reason, cancellationToken);

        public Task<WorkflowRun?> FindValidatedAsync(Guid workflowDefinitionId, string correlationId, CancellationToken cancellationToken) =>
            _inner.FindValidatedAsync(workflowDefinitionId, correlationId, cancellationToken);

        public Task<IReadOnlyCollection<WorkflowRun>> ListByDefinitionAsync(Guid workflowDefinitionId, CancellationToken cancellationToken) =>
            _inner.ListByDefinitionAsync(workflowDefinitionId, cancellationToken);

        public Task<IReadOnlyCollection<WorkflowRun>> ListRecentAsync(int count, CancellationToken cancellationToken) =>
            _inner.ListRecentAsync(count, cancellationToken);

        public Task<IReadOnlyDictionary<WorkflowRunStatus, int>> GetStatusCountsAsync(CancellationToken cancellationToken) =>
            _inner.GetStatusCountsAsync(cancellationToken);
    }

    private static WorkflowDefinition BuildValidSourceToSqlWorkflow(string name)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), name, 1);
        var source = AddNode(workflow, WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, 0);
        var deIdentification = AddNode(workflow, WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50);
        var mapping = AddNode(workflow, WorkflowNodeTypes.Mapping, WorkflowNodeCategory.Transform, 60);
        var destination = AddNode(workflow, WorkflowNodeTypes.SqlServerDestination, WorkflowNodeCategory.Destination, 70);
        workflow.AddEdge(source.Id, deIdentification.Id);
        workflow.AddEdge(deIdentification.Id, mapping.Id);
        workflow.AddEdge(mapping.Id, destination.Id);
        return workflow;
    }

    private static RankedWorkflowOrchestrator CreateOrchestrator(params IWorkflowNodeExecutor[] executors)
        => new(new WorkflowGraphValidator(), new WorkflowNodeExecutorRegistry(executors));

    private static WorkflowExecutionContext CreateContext()
        => new(Guid.NewGuid(), "test-correlation");

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

    private sealed class DeferringExecutor : IWorkflowNodeExecutor
    {
        private readonly Guid _deferredJobId;

        public DeferringExecutor(string nodeType, Guid deferredJobId)
        {
            NodeType = nodeType;
            _deferredJobId = deferredJobId;
        }

        public string NodeType { get; }

        public Task<WorkflowNodeOutput> ExecuteAsync(
            WorkflowExecutionContext context,
            WorkflowNode node,
            IReadOnlyCollection<WorkflowNodeOutput> inputs,
            CancellationToken cancellationToken)
            => Task.FromResult(new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                payload: null,
                WorkflowDataContract.None,
                new Dictionary<string, object?>
                {
                    [WorkflowNodeOutputMetadataKeys.BulkExportDeferredJobId] = _deferredJobId.ToString(),
                }));
    }

    private sealed class RecordingBulkExportPauseRecorder : IBulkExportPauseRecorder
    {
        public Guid? RecordedJobId { get; private set; }

        public string? RecordedPriorOutputsJson { get; private set; }

        public Task RecordPauseAsync(Guid bulkExportJobId, string priorNodeOutputsJson, CancellationToken cancellationToken)
        {
            RecordedJobId = bulkExportJobId;
            RecordedPriorOutputsJson = priorNodeOutputsJson;
            return Task.CompletedTask;
        }
    }

    private sealed class StubWorkflowDefinitionStore : IWorkflowDefinitionStore
    {
        private readonly WorkflowDefinition _workflowDefinition;

        public StubWorkflowDefinitionStore(WorkflowDefinition workflowDefinition)
        {
            _workflowDefinition = workflowDefinition;
        }

        public Task<WorkflowDefinition> SaveAsync(WorkflowDefinition workflowDefinition, CancellationToken cancellationToken)
            => Task.FromResult(workflowDefinition);

        public Task<IReadOnlyCollection<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<WorkflowDefinition>>([_workflowDefinition]);

        public Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken)
            => Task.FromResult(workflowId == _workflowDefinition.Id ? _workflowDefinition : null);

        public Task DeleteAsync(Guid workflowId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingExceptionManager : IGlobalExceptionManager
    {
        public List<ExceptionContext> CapturedContexts { get; } = [];

        public Task<ErrorReport> CaptureAsync(Exception exception, ExceptionContext context, CancellationToken cancellationToken = default)
        {
            CapturedContexts.Add(context);
            return Task.FromResult(new ErrorReport("ERR-TEST-000001", ErrorCategory.Unknown, "Something went wrong.", context.CorrelationId));
        }

        public Task<string?> CaptureExpectedAsync(ExpectedFailure failure, ExceptionContext context, CancellationToken cancellationToken = default)
        {
            CapturedContexts.Add(context);
            return Task.FromResult<string?>("ERR-TEST-000002");
        }

        // Workflow trace lines (WorkflowDebug) are not part of what these tests record.
        public Task<string?> CaptureTraceAsync(
            string severity, string entryType, string message, ExceptionContext context,
            DateTime? occurredUtc = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class PayloadExecutor : IWorkflowNodeExecutor
    {
        private readonly Func<IReadOnlyCollection<WorkflowNodeOutput>, object?> _execute;
        private readonly WorkflowDataContract _contract;
        private readonly IReadOnlyDictionary<string, object?>? _metadata;

        public PayloadExecutor(
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
}
