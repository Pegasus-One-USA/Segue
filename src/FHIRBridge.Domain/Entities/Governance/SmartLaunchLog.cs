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
        string? failureReason,
        string? grantedScope = null,
        bool? patientContextGranted = null,
        string? tokenCacheKeyHash = null)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        SourceConnectionId = sourceConnectionId;
        SourceName = sourceName;
        LaunchType = launchType;
        Success = success;
        FailureReason = failureReason;
        GrantedScope = grantedScope;
        PatientContextGranted = patientContextGranted;
        TokenCacheKeyHash = tokenCacheKeyHash;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public Guid SourceConnectionId { get; private set; }
    public string SourceName { get; private set; } = default!;
    public string LaunchType { get; private set; } = default!;
    public bool Success { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>The scope Epic actually granted (echoed back in the token response) — interactive sign-ins only.</summary>
    public string? GrantedScope { get; private set; }

    /// <summary>Whether the token response carried a <c>patient</c> claim — interactive sign-ins only. Null for
    /// launch types that never set it (e.g. a Backend Services JWT exchange).</summary>
    public bool? PatientContextGranted { get; private set; }

    /// <summary>Non-reversible hash of the token-cache key this session was saved under — lets a later lookup-time
    /// hash be compared to confirm the same session was actually reused, without ever printing the CallerId/session
    /// identifier itself.</summary>
    public string? TokenCacheKeyHash { get; private set; }
}
