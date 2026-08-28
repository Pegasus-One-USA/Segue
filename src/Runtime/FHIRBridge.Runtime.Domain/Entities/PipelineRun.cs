using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Domain.Entities;

public sealed class PipelineRun
{
    private readonly List<PipelineRunStep> _steps = [];

    public PipelineRun(
        RuntimeSourceType sourceType,
        RuntimeDestinationType destinationType,
        IReadOnlyList<string> requestedResourceTypes,
        string? triggeredBy,
        string? correlationId)
    {
        Id = Guid.NewGuid();
        SourceType = sourceType;
        DestinationType = destinationType;
        RequestedResourceTypes = requestedResourceTypes;
        TriggeredBy = triggeredBy;
        CorrelationId = correlationId;
        Status = PipelineRunStatus.Running;
        StartedOnUtc = DateTime.UtcNow;
    }

    public Guid Id { get; }
    public RuntimeSourceType SourceType { get; }
    public RuntimeDestinationType DestinationType { get; }
    public IReadOnlyList<string> RequestedResourceTypes { get; }
    public string? TriggeredBy { get; }
    public string? CorrelationId { get; }
    public PipelineRunStatus Status { get; private set; }
    public int ExtractedResourceCount { get; private set; }
    public int WrittenResourceCount { get; private set; }
    public string? FailureMessage { get; private set; }

    /// <summary>The Global Exception Manager's <c>ERR-yyyyMMdd-NNNNNN</c> id for this run's failure, when one was
    /// actually persisted to ErrorLogs — set via <see cref="SetErrorReference"/> after <see cref="Fail"/>. Null
    /// whenever no capture ran or the capture itself failed to persist (never a placeholder).</summary>
    public string? ErrorReferenceId { get; private set; }
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

    /// <summary>Records the Global Exception Manager's reference id for this run's failure. Called only after
    /// the capture call has actually returned — pass null (a no-op) when no exception manager was registered or
    /// the capture failed to persist.</summary>
    public void SetErrorReference(string? referenceId)
    {
        if (referenceId is not null)
        {
            ErrorReferenceId = referenceId;
        }
    }
}
