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
    PartialSuccess
}
