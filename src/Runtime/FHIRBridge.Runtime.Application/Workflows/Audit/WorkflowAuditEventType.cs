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
    WorkflowRunCancelled
}
