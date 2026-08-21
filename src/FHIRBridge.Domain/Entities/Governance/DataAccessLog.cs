using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Immutable record of a patient/resource access — the HIPAA §164.312(b) "who accessed which
/// patient's data, when, and why" evidence. Deliberately PHI-free: identifiers only, never
/// clinical content.
/// </summary>
public sealed class DataAccessLog : Entity<Guid>, IAppendOnlyEntity
{
    private DataAccessLog()
    {
    }

    public DataAccessLog(
        Guid id,
        DateTime occurredOnUtc,
        string actor,
        string resourceType,
        string? resourceId,
        string action,
        string? patientId,
        string? purpose,
        Guid? pipelineRunId,
        string? correlationId,
        string? ipAddress)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Actor = actor;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Action = action;
        PatientId = patientId;
        Purpose = purpose;
        PipelineRunId = pipelineRunId;
        CorrelationId = correlationId;
        IpAddress = ipAddress;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string Actor { get; private set; } = default!;
    public string ResourceType { get; private set; } = default!;
    public string? ResourceId { get; private set; }
    public string Action { get; private set; } = default!;
    public string? PatientId { get; private set; }
    public string? Purpose { get; private set; }
    public Guid? PipelineRunId { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? IpAddress { get; private set; }
}
