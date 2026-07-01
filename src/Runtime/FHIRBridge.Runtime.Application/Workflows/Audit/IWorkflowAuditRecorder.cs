namespace FHIRBridge.Runtime.Application.Workflows.Audit;

public interface IWorkflowAuditRecorder
{
    Task RecordAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken);
}
