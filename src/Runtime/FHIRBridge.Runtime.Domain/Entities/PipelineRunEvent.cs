using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Domain.Entities;

public sealed class PipelineRunEvent
{
    public PipelineRunEvent(
        Guid pipelineRunId,
        Guid tenantId,
        string eventType,
        PipelineStepType? stepType,
        string? resourceType,
        string? resourceId,
        string message,
        string? correlationId)
    {
        Id = Guid.NewGuid();
        PipelineRunId = pipelineRunId;
        TenantId = tenantId;
        EventType = eventType;
        StepType = stepType;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Message = message;
        CorrelationId = correlationId;
        OccurredOnUtc = DateTime.UtcNow;
    }

    public Guid Id { get; }
    public Guid PipelineRunId { get; }
    public Guid TenantId { get; }
    public string EventType { get; }
    public PipelineStepType? StepType { get; }
    public string? ResourceType { get; }
    public string? ResourceId { get; }
    public string Message { get; }
    public string? CorrelationId { get; }
    public DateTime OccurredOnUtc { get; }
}
