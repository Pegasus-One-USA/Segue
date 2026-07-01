using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Domain.Entities;

public sealed class PipelineRun
{
    private readonly List<PipelineRunStep> _steps = [];

    public PipelineRun(
        Guid tenantId,
        RuntimeSourceType sourceType,
        RuntimeDestinationType destinationType,
        IEnumerable<string> requestedResourceTypes,
        string? triggeredBy,
        string? correlationId)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        SourceType = sourceType;
        DestinationType = destinationType;
        RequestedResourceTypes = requestedResourceTypes.ToArray();
        TriggeredBy = triggeredBy;
        CorrelationId = correlationId;
        Status = PipelineRunStatus.Running;
        StartedOnUtc = DateTime.UtcNow;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public RuntimeSourceType SourceType { get; }
    public RuntimeDestinationType DestinationType { get; }
    public IReadOnlyList<string> RequestedResourceTypes { get; }
    public string? TriggeredBy { get; }
    public string? CorrelationId { get; }
    public PipelineRunStatus Status { get; private set; }
    public int ExtractedResourceCount { get; private set; }
    public int WrittenResourceCount { get; private set; }
    public string? FailureMessage { get; private set; }
    public DateTime StartedOnUtc { get; }
    public DateTime? CompletedOnUtc { get; private set; }
    public IReadOnlyCollection<PipelineRunStep> Steps => _steps.AsReadOnly();

    public PipelineRunStep StartStep(PipelineStepType stepType, string? resourceType)
    {
        var step = new PipelineRunStep(Id, stepType, resourceType);
        _steps.Add(step);
        return step;
    }

    public void AddExtractedResources(int count)
    {
        ExtractedResourceCount += count;
    }

    public void AddWrittenResources(int count)
    {
        WrittenResourceCount += count;
    }

    public void Complete()
    {
        Status = PipelineRunStatus.Completed;
        CompletedOnUtc = DateTime.UtcNow;
    }

    public void Fail(string message)
    {
        Status = PipelineRunStatus.Failed;
        FailureMessage = message;
        CompletedOnUtc = DateTime.UtcNow;
    }
}
