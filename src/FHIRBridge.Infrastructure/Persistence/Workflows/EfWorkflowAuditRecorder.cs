using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Runtime.Application.Workflows.Audit;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

/// <summary>
/// SQL-backed <see cref="IWorkflowAuditRecorder"/>. Replaces the in-process, restart-losing
/// <c>InMemoryWorkflowAuditRecorder</c> default with a durable, append-only table so Runtime-plane
/// pipeline events survive a process restart, mirroring the config-side <see cref="AuditLog"/> mechanism.
/// </summary>
public sealed class EfWorkflowAuditRecorder : IWorkflowAuditRecorder
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfWorkflowAuditRecorder(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        var entry = new WorkflowAuditLog(
            Guid.NewGuid(),
            auditEvent.EventType.ToString(),
            auditEvent.WorkflowId,
            auditEvent.WorkflowRunId,
            auditEvent.NodeId,
            auditEvent.NodeType,
            auditEvent.ResourceType,
            auditEvent.ResourceIdHash,
            auditEvent.InputContract.ToString(),
            auditEvent.OutputContract.ToString(),
            auditEvent.Timestamp,
            auditEvent.Message);

        await _dbContext.WorkflowAuditLogs.AddAsync(entry, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
