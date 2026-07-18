using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of a login, logout, MFA, or token event.</summary>
public sealed class AuthenticationLog : Entity<Guid>, IAppendOnlyEntity
{
    private AuthenticationLog()
    {
    }

    public AuthenticationLog(
        Guid id,
        DateTime occurredOnUtc,
        string? userEmail,
        string authenticationType,
        bool success,
        string? failureReason,
        string? ipAddress,
        string? userAgent,
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        UserEmail = userEmail;
        AuthenticationType = authenticationType;
        Success = success;
        FailureReason = failureReason;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        CorrelationId = correlationId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string? UserEmail { get; private set; }
    public string AuthenticationType { get; private set; } = default!;
    public bool Success { get; private set; }
    public string? FailureReason { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public string? CorrelationId { get; private set; }
}
