namespace FHIRBridge.Runtime.Application.Workflows.Audit;

public sealed class InMemoryWorkflowAuditRecorder : IWorkflowAuditRecorder
{
    private readonly List<WorkflowAuditEvent> _events = [];

    public IReadOnlyCollection<WorkflowAuditEvent> Events => _events;

    public Task RecordAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        _events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
