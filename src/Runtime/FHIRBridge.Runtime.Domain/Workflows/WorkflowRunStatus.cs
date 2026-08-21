namespace FHIRBridge.Runtime.Domain.Workflows;

public enum WorkflowRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>Every node executed, but at least one non-parent resource type on a source node was skipped
    /// because the app isn't authorized for it — everything else in the run still completed and was written.</summary>
    PartialSuccess,

    /// <summary>A source node deferred to an async FHIR Bulk Data <c>$export</c> job instead of blocking for the
    /// job's full duration — the run is paused past that node until <c>BulkExportPollWorker</c> observes the job
    /// complete and resumes execution from the next node.</summary>
    AwaitingBulkExport
}
