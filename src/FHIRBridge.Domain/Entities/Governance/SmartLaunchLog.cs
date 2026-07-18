using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Immutable record of a SMART on FHIR launch completing (or failing) — distinct from
/// <see cref="AuthenticationLog"/> because there is no FHIRBridge portal user involved: this is the EHR/patient
/// authorizing a source connection's data access, not someone logging into the portal.
/// </summary>
public sealed class SmartLaunchLog : Entity<Guid>, IAppendOnlyEntity
{
    private SmartLaunchLog()
    {
    }

    public SmartLaunchLog(
        Guid id,
        DateTime occurredOnUtc,
        Guid sourceConnectionId,
        string sourceName,
        string launchType,
        bool success,
        string? failureReason)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        SourceConnectionId = sourceConnectionId;
        SourceName = sourceName;
        LaunchType = launchType;
        Success = success;
        FailureReason = failureReason;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public Guid SourceConnectionId { get; private set; }
    public string SourceName { get; private set; } = default!;
    public string LaunchType { get; private set; } = default!;
    public bool Success { get; private set; }
    public string? FailureReason { get; private set; }
}
