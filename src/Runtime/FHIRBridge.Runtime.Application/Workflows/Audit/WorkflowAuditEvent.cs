namespace FHIRBridge.Runtime.Application.Workflows.Audit;

public sealed record WorkflowAuditEvent(
    WorkflowAuditEventType EventType,
    Guid WorkflowId,
    Guid WorkflowRunId,
    Guid? NodeId,
    string? NodeType,
    string? ResourceType,
    string? ResourceIdHash,
    WorkflowDataContract InputContract,
    WorkflowDataContract OutputContract,
    DateTimeOffset Timestamp,
    string? Message = null);
