using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Durable, append-only record of a Runtime-plane workflow event (extraction/transform/output step).
/// Mirrors <see cref="AuditLog"/>'s append-only guarantee for the Runtime DAG engine, whose audit trail
/// previously lived only in an in-process list and was lost on restart. Deliberately carries no PHI payload —
/// only identifiers, node/resource type labels, and a plain message.
/// </summary>
public sealed class WorkflowAuditLog : Entity<Guid>, IAppendOnlyEntity
{
    private WorkflowAuditLog()
    {
    }

    public WorkflowAuditLog(
        Guid id,
        string eventType,
        Guid workflowId,
        Guid workflowRunId,
        Guid? nodeId,
        string? nodeType,
        string? resourceType,
        string? resourceIdHash,
        string inputContract,
        string outputContract,
        DateTimeOffset occurredOnUtc,
        string? message)
    {
        Id = id;
        EventType = eventType;
        WorkflowId = workflowId;
        WorkflowRunId = workflowRunId;
        NodeId = nodeId;
        NodeType = nodeType;
        ResourceType = resourceType;
        ResourceIdHash = resourceIdHash;
        InputContract = inputContract;
        OutputContract = outputContract;
        OccurredOnUtc = occurredOnUtc;
        Message = message;
    }

    public string EventType { get; private set; } = default!;
    public Guid WorkflowId { get; private set; }
    public Guid WorkflowRunId { get; private set; }
    public Guid? NodeId { get; private set; }
    public string? NodeType { get; private set; }
    public string? ResourceType { get; private set; }
    public string? ResourceIdHash { get; private set; }
    public string InputContract { get; private set; } = default!;
    public string OutputContract { get; private set; } = default!;
    public DateTimeOffset OccurredOnUtc { get; private set; }
    public string? Message { get; private set; }
}
