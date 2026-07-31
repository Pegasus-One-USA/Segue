using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A durable checkpoint for one in-flight FHIR Bulk Data <c>$export</c> job: kicked off once, then polled to
/// completion across many <c>BulkExportPollWorker</c> ticks instead of blocking a request/message-handler thread
/// for the job's full duration (which can run to ~2 hours). <see cref="AuditableEntity{TId}"/> (not the plain
/// <see cref="Entity{TId}"/> most records here use) specifically for its <c>RowVersion</c> optimistic-concurrency
/// token — multiple Worker instances may race to claim the same due job, exactly like
/// <c>ResourcePipelineRoute</c>'s scheduler-claim race; the loser gets <c>DbUpdateConcurrencyException</c>.
/// </summary>
public sealed class BulkExportJob : AuditableEntity<Guid>
{
    private BulkExportJob()
    {
    }

    public BulkExportJob(
        Guid id,
        string sourcePath,
        Guid sourceConnectionId,
        Guid? sourceConfigurationId,
        string exportRequestJson,
        DateTime kickedOffOnUtc,
        string? correlationId = null,
        string? triggeredBy = null,
        Guid? workflowRunId = null,
        Guid? workflowNodeId = null,
        string? priorNodeOutputsJson = null,
        string? contextJson = null)
    {
        Id = id;
        SourcePath = sourcePath;
        SourceConnectionId = sourceConnectionId;
        SourceConfigurationId = sourceConfigurationId;
        ExportRequestJson = exportRequestJson;
        KickedOffOnUtc = kickedOffOnUtc;
        CorrelationId = correlationId;
        TriggeredBy = triggeredBy;
        WorkflowRunId = workflowRunId;
        WorkflowNodeId = workflowNodeId;
        PriorNodeOutputsJson = priorNodeOutputsJson;
        ContextJson = contextJson;
        Status = BulkExportJobStatus.Pending;
    }

    /// <summary>Discriminates which caller kicked this job off, so the poller's completion step knows which
    /// continuation to invoke. One of the <see cref="BulkExportJobSourcePath"/> constants.</summary>
    public string SourcePath { get; private set; } = default!;

    public string Status { get; private set; } = default!;

    /// <summary>Not the full <c>FhirSourceConfiguration</c> DTO, which carries plaintext secrets
    /// (private key/client secret) — credentials are re-resolved fresh on every poll tick via
    /// <c>SourceConnectionRuntimeResolver</c>, which also naturally handles token refresh across a long poll.</summary>
    public Guid SourceConnectionId { get; private set; }

    public Guid? SourceConfigurationId { get; private set; }

    /// <summary>Serialized <c>FhirBulkExportRequest</c> (scope/resourceTypes/since/typeFilter/groupId/patientIds —
    /// none of which are secret).</summary>
    public string ExportRequestJson { get; private set; } = default!;

    public string? StatusUrl { get; private set; }

    public int PollAttemptCount { get; private set; }

    /// <summary>Honors the server's Retry-After; the poller skips rows not yet due.</summary>
    public DateTime? NextPollNotBeforeUtc { get; private set; }

    public DateTime KickedOffOnUtc { get; private set; }

    public DateTime? CompletedOnUtc { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? TriggeredBy { get; private set; }

    /// <summary>Set only for <see cref="BulkExportJobSourcePath.WorkflowNode"/> jobs — the run being paused.</summary>
    public Guid? WorkflowRunId { get; private set; }

    /// <summary>Set only for <see cref="BulkExportJobSourcePath.WorkflowNode"/> jobs — the source node that deferred.</summary>
    public Guid? WorkflowNodeId { get; private set; }

    /// <summary>Snapshot of every node output already computed before the deferring node, so the run can resume
    /// past it without recomputing. JSON: <c>{ nodeId: { nodeType, contract, payloadJson, metadata } }</c>.</summary>
    public string? PriorNodeOutputsJson { get; private set; }

    /// <summary>Serialized <c>WorkflowExecutionContext</c> so the resumed run can rebuild it (trigger info, target
    /// patient, caller id, etc.) — none of this survives as ambient/in-memory state across a Worker tick boundary.</summary>
    public string? ContextJson { get; private set; }

    public void RecordPriorNodeOutputs(string priorNodeOutputsJson)
    {
        PriorNodeOutputsJson = priorNodeOutputsJson;
    }

    public void MarkKickedOff(string statusUrl)
    {
        Status = BulkExportJobStatus.Polling;
        StatusUrl = statusUrl;
    }

    public void RecordPollAttempt(DateTime nextPollNotBeforeUtc)
    {
        PollAttemptCount++;
        NextPollNotBeforeUtc = nextPollNotBeforeUtc;
    }

    public void MarkCompleted(DateTime completedOnUtc)
    {
        Status = BulkExportJobStatus.Completed;
        CompletedOnUtc = completedOnUtc;
    }

    public void MarkFailed(string errorMessage, DateTime completedOnUtc)
    {
        Status = BulkExportJobStatus.Failed;
        ErrorMessage = errorMessage;
        CompletedOnUtc = completedOnUtc;
    }
}

/// <summary>Values for <see cref="BulkExportJob.SourcePath"/>.</summary>
public static class BulkExportJobSourcePath
{
    /// <summary>Kicked off by a Runtime-plane <c>PipelineOrchestrator</c> bulk-export run.</summary>
    public const string Orchestrator = "Orchestrator";

    /// <summary>Kicked off by <c>ConfiguredPipelineService</c>'s message-handler-driven path.</summary>
    public const string ConfiguredPipeline = "ConfiguredPipeline";

    /// <summary>Kicked off by a <c>SourceNode</c> inside a portal workflow-builder run (<c>RankedWorkflowOrchestrator</c>).</summary>
    public const string WorkflowNode = "WorkflowNode";
}

/// <summary>Values for <see cref="BulkExportJob.Status"/>.</summary>
public static class BulkExportJobStatus
{
    public const string Pending = "Pending";
    public const string Polling = "Polling";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
