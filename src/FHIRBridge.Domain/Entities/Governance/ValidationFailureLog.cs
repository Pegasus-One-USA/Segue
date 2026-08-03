using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Immutable record of a resource that produced US-Core/data-quality validation warnings during normalization.
/// <see cref="Warnings"/> is a JSON array of structural warning strings (field-name/rule descriptions) —
/// PHI-free by construction, unlike the fetched/normalized/mapped payloads on <c>PipelineRunResourceRecord</c>.
/// </summary>
public sealed class ValidationFailureLog : Entity<Guid>, IAppendOnlyEntity
{
    private ValidationFailureLog()
    {
    }

    public ValidationFailureLog(
        Guid id,
        DateTime occurredOnUtc,
        string resourceType,
        string? resourceId,
        string warningsJson,
        double? dataQualityScore,
        Guid? pipelineRunId,
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        ResourceType = resourceType;
        ResourceId = resourceId;
        WarningsJson = warningsJson;
        DataQualityScore = dataQualityScore;
        PipelineRunId = pipelineRunId;
        CorrelationId = correlationId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string ResourceType { get; private set; } = default!;
    public string? ResourceId { get; private set; }
    public string WarningsJson { get; private set; } = default!;
    public double? DataQualityScore { get; private set; }
    public Guid? PipelineRunId { get; private set; }
    public string? CorrelationId { get; private set; }
}
