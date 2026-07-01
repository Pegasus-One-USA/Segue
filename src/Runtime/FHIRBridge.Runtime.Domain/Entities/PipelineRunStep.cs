using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Domain.Entities;

public sealed class PipelineRunStep
{
    internal PipelineRunStep(
        Guid pipelineRunId,
        PipelineStepType stepType,
        string? resourceType)
    {
        Id = Guid.NewGuid();
        PipelineRunId = pipelineRunId;
        StepType = stepType;
        ResourceType = resourceType;
        Status = PipelineStepStatus.Running;
        StartedOnUtc = DateTime.UtcNow;
    }

    public Guid Id { get; }
    public Guid PipelineRunId { get; }
    public PipelineStepType StepType { get; }
    public PipelineStepStatus Status { get; private set; }
    public string? ResourceType { get; }
    public int ResourceCount { get; private set; }
    public string? Message { get; private set; }
    public DateTime StartedOnUtc { get; }
    public DateTime? CompletedOnUtc { get; private set; }

    public void Complete(int resourceCount, string? message = null)
    {
        Status = PipelineStepStatus.Completed;
        ResourceCount = resourceCount;
        Message = message;
        CompletedOnUtc = DateTime.UtcNow;
    }

    public void Fail(string message)
    {
        Status = PipelineStepStatus.Failed;
        Message = message;
        CompletedOnUtc = DateTime.UtcNow;
    }
}
