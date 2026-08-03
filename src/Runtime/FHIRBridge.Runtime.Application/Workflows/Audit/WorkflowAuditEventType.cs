namespace FHIRBridge.Runtime.Application.Workflows.Audit;

public enum WorkflowAuditEventType
{
    WorkflowRunStarted,
    NodeExecutionStarted,
    NodeExecutionCompleted,
    NodeExecutionFailed,
    WorkflowRunCompleted,
    WorkflowRunFailed,
    NodeExecutionCancelled,
    WorkflowRunCancelled,

    /// <summary>A source node deferred to an async bulk-export job; the run is paused until a poller resumes it.</summary>
    WorkflowRunAwaitingBulkExport
}
